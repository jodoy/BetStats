using System.Data;
using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Football;
using BetStats.Application.Ingestion;
using BetStats.Application.Governance;
using BetStats.Domain.Governance;
using BetStats.Infrastructure.Datasets;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Evaluation;

public sealed class PostgreSqlBacktests(BetStatsDbContext db, IFootballResultDatasets datasets, IResultDatasetOperations datasetOperations,
    IDatasets metadata, IFootballResults results, IResultGovernance governance, IRawPayloadStore store,
    IHistoricalPredictionProvider predictor, ISourcePolicyEvaluator policies, TimeSpan? leaseDuration = null) : IHistoricalBacktests
{
    private readonly TimeSpan lease = leaseDuration ?? TimeSpan.FromMinutes(10);
    private IHistoricalPredictionProvider Predictor(BacktestDefinition definition) => definition.Model is { } model ? new BetStats.Application.Models.FootballPredictor(model) : predictor;
    private Task Lock(Guid id, CancellationToken token) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({id.ToString("D")},9011))", token);
    private Task<BacktestOperationEvent?> Latest(Guid id, CancellationToken token) => db.BacktestOperations.AsNoTracking().Where(e => e.OperationId == id).OrderByDescending(e => e.Sequence).FirstOrDefaultAsync(token);
    private async Task<ResultOperationResult> Result(BacktestOperationEvent e, CancellationToken token) => new(e.OperationId, e.Status, e.Sequence, e.SnapshotId,
        e.SnapshotId is { } id ? await db.Backtests.Where(a => a.Id == id).Select(a => a.Hash).SingleAsync(token) : null, e.FailureCode, e.Fingerprint);
    private BacktestOperationEvent Add(Guid operation, int sequence, ResultOperationStatus status, string fingerprint, byte[] bytes, Guid owner,
        DateTime? until, Guid? snapshot, string? failure, string actor, string reason)
    {
        var e = new BacktestOperationEvent { Id = Guid.NewGuid(), OperationId = operation, Sequence = sequence, Status = status, Fingerprint = fingerprint, Request = bytes,
            OwnerToken = owner, LeaseUntilUtc = until, SnapshotId = snapshot, FailureCode = failure, OperatorId = actor, Reason = reason };
        db.Add(e); return e;
    }
    private async Task<FootballResultSnapshot> Features(BacktestDefinition definition, CancellationToken token)
    {
        var verified = await datasetOperations.VerifyAsync(definition.DatasetId, true, token);
        if (!verified.CurrentlyAuthorized) throw new UnauthorizedAccessException("Feature dataset current permission denied.");
        if (!verified.Integrity || !verified.FeaturesReproducible || verified.RawAvailable != true || verified.RawHashVerified != true) throw new InvalidDataException("Feature dataset/RAW verification failed.");
        var snapshot = await ReadFeatureSnapshot(definition.DatasetId, token);
        if (snapshot.Hash != definition.ExpectedDatasetHash) throw new InvalidDataException("Feature dataset fingerprint mismatch.");
        return snapshot;
    }
    private async Task<FootballResultSnapshot> ReadFeatureSnapshot(Guid id, CancellationToken token)
    {
        try { return await datasets.InspectAsync(id, token); }
        catch (Exception error) when (PostgreSqlDatasets.IsPermissionDenial(error)) { throw new UnauthorizedAccessException("Current feature permission denied.", error); }
    }
    public async Task<BacktestManifest> PlanAsync(BacktestDefinition definition, CancellationToken token = default)
    {
        definition.Validate();
        var features = await Features(definition, token); var d = features.Manifest.MetadataManifest.Definition;
        if (definition.EvaluationCutoffUtc > await QualityPersistence.Now(db, token)) throw new ArgumentException("Evaluation cannot consume a future cutoff.");
        var interpretations = definition.Evaluations.Select(e => new { e.Mode, e.ReconstructionUtc }).Distinct().ToArray();
        if (interpretations.Length != 1) throw new ArgumentException("One explicit label interpretation boundary per backtest required.");
        var interpretation = interpretations[0];
        await using var assembly = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var rows = new List<FootballResultDatasetRow>();
        foreach (var row in features.Manifest.Rows)
        {
            var labels = await results.ReadAsync(new(d.CompetitionId, d.SeasonId, definition.EvaluationCutoffUtc, d.Purpose, d.Context, row.Metadata.EventId,
                row.Metadata.Target.SourceId, interpretation.Mode, interpretation.ReconstructionUtc), token);
            rows.Add(row with { LabelEvidence = labels, Labels = labels.Results.Where(e => e.Eligible && e.Labels is not null).Select(e => e.Labels!).OrderBy(e => e.ResultObservationId).ToArray() });
        }
        var evidence = features.Manifest with { Rows = rows, LabelAsOfUtc = definition.EvaluationCutoffUtc, ResultGovernance = null };
        evidence = evidence with { ResultGovernance = await governance.FreezeAsync(evidence, token) };
        var authorizations = new List<BacktestAuthorization>(); var checkedAt = await QualityPersistence.Now(db, token);
        foreach (var source in Sources(evidence))
            foreach (var purpose in new[] { d.Purpose, DataPurpose.RawPayloadStorage, DataPurpose.HistoricalRetention, DataPurpose.InternalAnalytics }.Distinct().Order())
            {
                var decision = await policies.EvaluateAsync(source, purpose, checkedAt, d.Context, token);
                if (!decision.Allowed || decision.PolicyId is not { } policy || decision.Version is not { } version) throw new UnauthorizedAccessException("Current backtest policy denied.");
                var ids = await db.PolicyAudits.AsNoTracking().Where(a => a.SourcePolicyId == policy && a.RecordedAtUtc <= checkedAt && a.ReviewedAtUtc <= checkedAt).OrderBy(a => a.Id).Select(a => a.Id).ToArrayAsync(token);
                authorizations.Add(new(source, purpose, policy, version, ids));
            }
        var manifest = new BacktestExecutor(Predictor(definition)).Execute(definition, features, evidence) with { Authorizations = authorizations };
        await assembly.CommitAsync(token); return manifest;
    }
    public Task<ResultOperationResult> RunAsync(BacktestRequest request, CancellationToken token = default) => Run(request, false, token);
    public async Task<ResultOperationResult> RecoverAsync(ResultRecoveryRequest request, CancellationToken token = default)
    {
        QualityPersistence.Operator(request.OperatorId, request.Reason);
        if (!request.Approved || !BacktestRules.Hash(request.ExpectedFingerprint) || request.OperationId == Guid.Empty) throw new ArgumentException("Explicit approved recovery and expected fingerprint required.");
        var e = await Latest(request.OperationId, token) ?? throw new KeyNotFoundException("Backtest operation not found.");
        if (e.Fingerprint != request.ExpectedFingerprint) throw new InvalidOperationException("Operation fingerprint mismatch.");
        return await Run(new(request.OperationId, CanonicalDatasetJson.Deserialize<BacktestDefinition>(e.Request), request.OperatorId, request.Reason, true), true, token);
    }
    private async Task<ResultOperationResult> Run(BacktestRequest request, bool recovery, CancellationToken token)
    {
        QualityPersistence.Operator(request.OperatorId, request.Reason); request.Definition.Validate();
        if (!request.Approved || request.OperationId == Guid.Empty || !OperationFencing.ValidLease(lease)) throw new ArgumentException("Explicit operation UUID, approval and bounded lease required.");
        var bytes = CanonicalDatasetJson.Serialize(request.Definition); var fingerprint = CanonicalDatasetJson.Hash(bytes);
        if (bytes.Length > 1048576) throw new ArgumentException("Backtest request exceeds bound.");
        BacktestOperationEvent running;
        await using (var claim = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token))
        {
            await Lock(request.OperationId, token); var latest = await Latest(request.OperationId, token); var now = await QualityPersistence.Now(db, token);
            if (latest is not null && latest.Fingerprint != fingerprint) throw new InvalidOperationException("Operation fingerprint mismatch.");
            if (latest?.Status == ResultOperationStatus.Succeeded)
            {
                await claim.CommitAsync(token); _ = await InspectAsync(latest.SnapshotId!.Value, token); return await Result(latest, token);
            }
            if (latest is not null && (!recovery || latest.Status == ResultOperationStatus.Running && latest.LeaseUntilUtc > now))
            { await claim.CommitAsync(token); return await Result(latest, token); }
            var sequence = latest?.Sequence ?? 0;
            if (latest is null)
            {
                Add(request.OperationId, ++sequence, ResultOperationStatus.Requested, fingerprint, bytes, Guid.Empty, null, null, null, request.OperatorId, request.Reason);
                await db.SaveChangesAsync(token);
            }
            running = Add(request.OperationId, ++sequence, ResultOperationStatus.Running, fingerprint, bytes, Guid.NewGuid(), now + lease, null,
                recovery ? "explicit_recovery" : null, request.OperatorId, request.Reason);
            await db.SaveChangesAsync(token); await claim.CommitAsync(token);
        }
        try
        {
            var manifest = await PlanAsync(request.Definition, token); var content = CanonicalDatasetJson.Serialize(manifest); var hash = CanonicalDatasetJson.Hash(content);
            if (content.Length > 16777216) throw new InvalidDataException("Backtest artifact bound exceeded.");
            await using var publication = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            // Match BS-010 lock order: content, operation, sorted sources. No RAW I/O during publication.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({hash},9012))", token);
            await Lock(request.OperationId, token);
            var current = await Latest(request.OperationId, token); var now = await QualityPersistence.Now(db, token);
            if (current is null || !OperationFencing.Owns(current.Status, current.OwnerToken, running.OwnerToken, current.LeaseUntilUtc, now)) throw new InvalidOperationException("Backtest lease lost; publication fenced.");
            await EnsureCurrent(manifest, token, publication: true);
            var a = await db.Backtests.AsNoTracking().SingleOrDefaultAsync(a => a.Hash == hash, token);
            if (a is not null && !a.Content.AsSpan().SequenceEqual(content)) throw new InvalidDataException("Backtest content key mismatch.");
            if (a is null)
            {
                a = new() { Id = Guid.NewGuid(), DatasetId = request.Definition.DatasetId, Hash = hash, Content = content };
                db.Add(a); await db.SaveChangesAsync(token);
            }
            Add(request.OperationId, current.Sequence + 1, ResultOperationStatus.Succeeded, fingerprint, bytes, running.OwnerToken, null, a.Id, null, request.OperatorId, request.Reason);
            await db.SaveChangesAsync(token); await publication.CommitAsync(token);
            return await Result((await Latest(request.OperationId, CancellationToken.None))!, CancellationToken.None);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or IOException or OperationCanceledException or DbUpdateException or System.Data.Common.DbException || PostgreSqlDatasets.IsPermissionDenial(error))
        {
            db.ChangeTracker.Clear();
            await using var failure = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
            await Lock(request.OperationId, CancellationToken.None); var current = (await Latest(request.OperationId, CancellationToken.None))!;
            if (OperationFencing.Owns(current.Status, current.OwnerToken, running.OwnerToken, current.LeaseUntilUtc, await QualityPersistence.Now(db, CancellationToken.None)))
            {
                current = Add(request.OperationId, current.Sequence + 1, error is OperationCanceledException ? ResultOperationStatus.Cancelled : ResultOperationStatus.Failed,
                    fingerprint, bytes, running.OwnerToken, null, null, PostgreSqlDatasets.IsPermissionDenial(error) ? "source_permission_denied" : error is OperationCanceledException ? "cancelled" : "assembly_or_integrity_failure", request.OperatorId, request.Reason);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            await failure.CommitAsync(CancellationToken.None); return await Result(current, CancellationToken.None);
        }
    }
    private static IEnumerable<Guid> Sources(FootballResultManifest evidence) => evidence.Rows.SelectMany(r => r.Metadata.History.Prepend(r.Metadata.Target)).Select(e => e.SourceId)
        .Concat(evidence.Rows.SelectMany(r => r.FeatureEvidence.Results.Concat(r.LabelEvidence.Results)).Select(e => e.Observation.SourceId))
        .Concat(evidence.ResultGovernance?.Rows.SelectMany(r => r.FeatureCoverage.Items.Concat(r.LabelCoverage.Items)).Select(i => i.Evidence.SourceId) ?? [])
        .Concat(evidence.ResultGovernance?.Rows.SelectMany(r => r.Ends).Select(e => e.Evidence.SourceId) ?? []).Distinct().Order();
    private async Task EnsureCurrent(BacktestManifest m, CancellationToken token, bool publication = false)
    {
        var evidence = m.EvaluationEvidence; var d = evidence.MetadataManifest.Definition;
        foreach (var source in Sources(evidence)) await QualityPersistence.Lock(db, source, token);
        foreach (var reference in m.Authorizations ?? throw new InvalidDataException("Frozen current authorization provenance required."))
        {
            var decision = await policies.EvaluateAsync(reference.SourceId, reference.Purpose, await QualityPersistence.Now(db, token), d.Context, token);
            if (!decision.Allowed) throw new UnauthorizedAccessException("Current backtest policy denied.");
            // A changed current policy must create a new plan, rather than publishing stale authorization references.
            if (publication && (decision.PolicyId != reference.PolicyId || decision.Version != reference.PolicyVersion)) throw new InvalidOperationException("Authorization changed since assembly; re-plan explicitly.");
        }
        if (metadata is not PostgreSqlDatasets adapter) throw new InvalidOperationException("Transactional metadata adapter required.");
        await adapter.EnsureCurrentFrozenAsync(evidence.MetadataManifest, token);
        await results.EnsureCurrentAsync(evidence.Rows.SelectMany(r => r.FeatureEvidence.Results.Concat(r.LabelEvidence.Results)), d.Purpose, d.Context, token);
        if (evidence.ResultGovernance is { } g) await governance.EnsureCurrentAsync(g, evidence, token);
    }
    public async Task<ResultOperationResult> OperationAsync(Guid id, CancellationToken token = default) => await Result(await Latest(id, token) ?? throw new KeyNotFoundException("Backtest operation not found."), token);
    public async Task<BacktestSnapshot> InspectAsync(Guid id, CancellationToken token = default)
    {
        var a = await db.Backtests.AsNoTracking().SingleAsync(a => a.Id == id, token); var m = CanonicalDatasetJson.Deserialize<BacktestManifest>(a.Content);
        if (a.Hash != CanonicalDatasetJson.Hash(a.Content) || m.Definition.DatasetId != a.DatasetId || m.Version != 1 || m.SerializerVersion != 1) throw new InvalidDataException("Backtest integrity failure.");
        _ = await ReadFeatureSnapshot(a.DatasetId, token);
        try { await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token); await EnsureCurrent(m, token); await tx.CommitAsync(token); }
        catch (Exception error) when (PostgreSqlDatasets.IsPermissionDenial(error)) { throw new UnauthorizedAccessException("Current backtest permission denied.", error); }
        return new(a.Id, a.Hash, a.RecordedAtUtc, m);
    }
    public async Task<BacktestVerification> VerifyAsync(Guid id, bool deep = false, CancellationToken token = default)
    {
        var a = await db.Backtests.AsNoTracking().SingleAsync(a => a.Id == id, token); var m = CanonicalDatasetJson.Deserialize<BacktestManifest>(a.Content);
        var integrity = a.Hash == CanonicalDatasetJson.Hash(a.Content) && CanonicalDatasetJson.Serialize(m).AsSpan().SequenceEqual(a.Content) && m.Version == 1 && m.SerializerVersion == 1 && m.Definition.DatasetId == a.DatasetId;
        integrity &= m.Authorizations is { Count: > 0 };
        foreach (var reference in m.Authorizations ?? [])
        {
            integrity &= await db.SourcePolicies.AnyAsync(p => p.Id == reference.PolicyId && p.DataSourceId == reference.SourceId && p.Version == reference.PolicyVersion, token);
            integrity &= reference.AuditIds.Count > 0 && await db.PolicyAudits.CountAsync(p => reference.AuditIds.Contains(p.Id) && p.SourcePolicyId == reference.PolicyId, token) == reference.AuditIds.Count;
        }
        var feature = await datasetOperations.VerifyAsync(a.DatasetId, deep, token);
        integrity &= feature.Integrity && m.EvaluationEvidence.ResultGovernance is { } g && await governance.VerifyFrozenAsync(g, token);
        if (!feature.CurrentlyAuthorized) return new(integrity, false, false, null, null);
        FootballResultSnapshot frozen;
        try { frozen = await ReadFeatureSnapshot(a.DatasetId, token); }
        catch (UnauthorizedAccessException) { return new(integrity, false, false, null, null); }
        var calculated = new BacktestExecutor(Predictor(m.Definition)).Execute(m.Definition, frozen, m.EvaluationEvidence) with { Authorizations = m.Authorizations };
        var reproducible = feature.FeaturesReproducible && CanonicalDatasetJson.Fingerprint(calculated) == a.Hash;
        try { _ = await InspectAsync(id, token); }
        catch (UnauthorizedAccessException) { return new(integrity, reproducible, false, null, null); }
        if (!deep) return new(integrity, reproducible, true, null, null);
        var available = feature.RawAvailable == true; var hashes = feature.RawHashVerified == true;
        var ids = m.EvaluationEvidence.Rows.SelectMany(r => r.LabelEvidence.Results).Select(e => e.Observation.RawId)
            .Concat(m.EvaluationEvidence.ResultGovernance!.Rows.SelectMany(r => r.FeatureCoverage.Items.Concat(r.LabelCoverage.Items)).Select(i => i.Evidence.RawId))
            .Concat(m.EvaluationEvidence.ResultGovernance.Rows.SelectMany(r => r.Ends).Select(e => e.Evidence.RawId)).Distinct().Order();
        foreach (var rawId in ids)
        {
            // Authorization is checked immediately before each additional label/coverage/end RAW read.
            try
            {
                await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token)) { await EnsureCurrent(m, token); await tx.CommitAsync(token); }
                var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == rawId, token);
                if (raw.ByteLength is not { } length) { hashes = false; continue; }
                await store.ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, length), token);
            }
            catch (Exception error) when (PostgreSqlDatasets.IsPermissionDenial(error)) { return new(integrity, reproducible, false, null, null); }
            catch (FileNotFoundException) { available = false; hashes = false; }
            catch (DirectoryNotFoundException) { available = false; hashes = false; }
            catch (Exception error) when (error is IOException or InvalidDataException) { hashes = false; }
        }
        return new(integrity, reproducible, true, available, hashes);
    }
}
