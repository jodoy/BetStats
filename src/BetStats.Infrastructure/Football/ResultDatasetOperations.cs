using System.Data;
using BetStats.Application.Datasets;
using BetStats.Application.Football;
using BetStats.Application.Ingestion;
using BetStats.Application.Governance;
using BetStats.Application.Providers;
using BetStats.Domain.Governance;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Football;

public sealed class ResultDatasetOperations(BetStatsDbContext db, IFootballResultDatasets datasets, IDatasets metadata,
    IRawPayloadStore store, IResultGovernance governance, ISourcePolicyEvaluator policies, ISourceOperationalStatus sources, TimeSpan? leaseDuration = null) : IResultDatasetOperations
{
    private readonly TimeSpan lease = leaseDuration ?? TimeSpan.FromMinutes(10);
    public static string Fingerprint(FootballResultDatasetRequest request) => CanonicalDatasetJson.Fingerprint(new { GovernanceVersion = 1, request.Metadata.Definition, request.LabelAsOfUtc });
    private Task Lock(Guid operation, CancellationToken token) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({operation.ToString("D")}, 9010))", token);
    private Task<ResultOperationEvent?> Latest(Guid operation, CancellationToken token) => db.ResultOperations.AsNoTracking().Where(e => e.OperationId == operation).OrderByDescending(e => e.Sequence).FirstOrDefaultAsync(token);
    private async Task<ResultOperationResult> Result(ResultOperationEvent e, CancellationToken token) => new(e.OperationId, e.Status, e.Sequence, e.SnapshotId,
        e.SnapshotId is { } id ? await db.FootballResultArtifacts.Where(a => a.Id == id).Select(a => a.Hash).SingleAsync(token) : null, e.FailureCode, e.Fingerprint);
    private ResultOperationEvent Add(Guid operation, int sequence, ResultOperationStatus status, string fingerprint, byte[] request, Guid owner,
        DateTime? until, Guid? snapshot, string? failure, string actor, string reason)
    {
        var e = new ResultOperationEvent { Id = Guid.NewGuid(), OperationId = operation, Sequence = sequence, Status = status, Fingerprint = fingerprint, Request = request,
            OwnerToken = owner, LeaseUntilUtc = until, SnapshotId = snapshot, FailureCode = failure, OperatorId = actor, Reason = reason };
        db.Add(e); return e;
    }
    public Task<ResultOperationResult> BuildAsync(ResultOperationRequest request, CancellationToken token = default) => Run(request, false, null, token);
    public async Task<ResultOperationResult> RecoverAsync(ResultRecoveryRequest request, CancellationToken token = default)
    {
        QualityPersistence.Operator(request.OperatorId, request.Reason);
        if (!request.Approved || request.OperationId == Guid.Empty || request.ExpectedFingerprint.Length != 64) throw new ArgumentException("Explicit approved recovery and expected fingerprint required.");
        var existing = await Latest(request.OperationId, token) ?? throw new KeyNotFoundException("Result operation not found.");
        if (existing.Fingerprint != request.ExpectedFingerprint) throw new InvalidOperationException("Operation fingerprint mismatch.");
        var dataset = CanonicalDatasetJson.Deserialize<FootballResultDatasetRequest>(existing.Request);
        return await Run(new(request.OperationId, dataset, request.OperatorId, request.Reason, true), true, request.ExpectedFingerprint, token);
    }
    private async Task<ResultOperationResult> Run(ResultOperationRequest request, bool recover, string? expected, CancellationToken token)
    {
        QualityPersistence.Operator(request.OperatorId, request.Reason); request.Dataset.Metadata.Definition.Validate();
        if (!request.Approved || request.OperationId == Guid.Empty || !OperationFencing.ValidLease(lease)) throw new ArgumentException("Explicit operation UUID, approval and bounded lease required.");
        var fingerprint = Fingerprint(request.Dataset); var bytes = CanonicalDatasetJson.Serialize(request.Dataset);
        if (bytes.Length > 1024 * 1024) throw new ArgumentException("Result operation request exceeds bound.");
        ResultOperationEvent running;
        await using (var claim = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token))
        {
            await Lock(request.OperationId, token); var latest = await Latest(request.OperationId, token); var now = await QualityPersistence.Now(db, token);
            if (latest is not null && (latest.Fingerprint != fingerprint || expected is not null && latest.Fingerprint != expected)) throw new InvalidOperationException("Operation fingerprint mismatch.");
            if (latest?.Status == ResultOperationStatus.Succeeded)
            {
                await claim.CommitAsync(token); _ = await datasets.InspectAsync(latest.SnapshotId!.Value, token); return await Result(latest, token);
            }
            if (latest is not null && (!recover || latest.Status == ResultOperationStatus.Running && latest.LeaseUntilUtc > now))
            { await claim.CommitAsync(token); return await Result(latest, token); }
            var sequence = latest?.Sequence ?? 0;
            if (latest is null)
            {
                Add(request.OperationId, ++sequence, ResultOperationStatus.Requested, fingerprint, bytes, Guid.Empty, null, null, null, request.OperatorId, request.Reason);
                await db.SaveChangesAsync(token);
            }
            else bytes = latest.Request;
            running = Add(request.OperationId, ++sequence, ResultOperationStatus.Running, fingerprint, bytes, Guid.NewGuid(), now + lease, null,
                recover ? "explicit_recovery" : null, request.OperatorId, request.Reason);
            await db.SaveChangesAsync(token); await claim.CommitAsync(token);
        }
        try
        {
            if (datasets is not PostgreSqlFootballResultDatasets adapter) throw new InvalidOperationException("Transactional result dataset adapter required.");
            // Metadata attempts retain their own audit. Operation identity is separate from content identity.
            var dataset = request.Dataset with { Metadata = request.Dataset.Metadata with { OperatorId = request.OperatorId, Reason = request.Reason } };
            await adapter.BuildCoreAsync(dataset, true, async (snapshot, cancellation) =>
            {
                await Lock(request.OperationId, cancellation); var current = await Latest(request.OperationId, cancellation); var now = await QualityPersistence.Now(db, cancellation);
                if (current is null || !OperationFencing.Owns(current.Status, current.OwnerToken, running.OwnerToken, current.LeaseUntilUtc, now))
                    throw new InvalidOperationException("Result operation lease lost; publication fenced.");
                Add(request.OperationId, current.Sequence + 1, ResultOperationStatus.Succeeded, fingerprint, bytes, running.OwnerToken, null, snapshot, null, request.OperatorId, request.Reason);
                await db.SaveChangesAsync(cancellation);
            }, token);
            return await Result((await Latest(request.OperationId, CancellationToken.None))!, CancellationToken.None);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or OperationCanceledException or DbUpdateException or System.Data.Common.DbException)
        {
            db.ChangeTracker.Clear();
            await using var failure = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
            await Lock(request.OperationId, CancellationToken.None); var current = (await Latest(request.OperationId, CancellationToken.None))!;
            if (current.Status == ResultOperationStatus.Running && current.OwnerToken == running.OwnerToken && current.LeaseUntilUtc > await QualityPersistence.Now(db, CancellationToken.None))
            {
                current = Add(request.OperationId, current.Sequence + 1, error is OperationCanceledException ? ResultOperationStatus.Cancelled : ResultOperationStatus.Failed,
                    fingerprint, bytes, running.OwnerToken, null, null, error is UnauthorizedAccessException ? "source_permission_denied" : error is OperationCanceledException ? "cancelled" : "assembly_or_integrity_failure", request.OperatorId, request.Reason);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            await failure.CommitAsync(CancellationToken.None); return await Result(current, CancellationToken.None);
        }
    }
    public async Task<ResultOperationResult> InspectAsync(Guid operationId, CancellationToken token = default) =>
        await Result(await Latest(operationId, token) ?? throw new KeyNotFoundException("Result operation not found."), token);
    public async Task<FootballResultVerification> VerifyAsync(Guid snapshotId, bool deep = false, CancellationToken token = default)
    {
        var verification = await datasets.VerifyAsync(snapshotId, token);
        if (!deep || !verification.CurrentlyAuthorized) return verification;
        var snapshot = await datasets.InspectAsync(snapshotId, token);
        var artifact = await db.FootballResultArtifacts.AsNoTracking().SingleAsync(a => a.Id == snapshotId, token);
        var baseline = await metadata.VerifyDeepAsync(artifact.MetadataSnapshotId, token);
        if (!baseline.CurrentUseAuthorized) return verification with { CurrentlyAuthorized = false };
        var available = baseline.RawAvailable == true; var hashes = baseline.RawHashVerified == true;
        var evidence = snapshot.Manifest.Rows.SelectMany(r => r.FeatureEvidence.Results.Concat(r.LabelEvidence.Results));
        var rawIds = evidence.Select(e => e.Observation.RawId)
            .Concat(snapshot.Manifest.ResultGovernance?.Rows.SelectMany(r => r.FeatureCoverage.Items.Concat(r.LabelCoverage.Items)).Select(i => i.Evidence.RawId) ?? [])
            .Concat(snapshot.Manifest.ResultGovernance?.Rows.SelectMany(r => r.Ends).Select(e => e.Evidence.RawId) ?? []).Distinct().Order().ToArray();
        foreach (var id in rawIds)
        {
            var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == id, token);
            try
            {
                // Recheck immediately before each RAW read, including current retention restrictions.
                var now = await QualityPersistence.Now(db, token);
                if (await sources.ReadAsync(raw.DataSourceId, token) != SourceOperationalStatus.Enabled) return verification with { CurrentlyAuthorized = false };
                foreach (var purpose in new[] { snapshot.Manifest.MetadataManifest.Definition.Purpose, DataPurpose.RawPayloadStorage, DataPurpose.HistoricalRetention, DataPurpose.InternalAnalytics }.Distinct())
                {
                    var decision = await policies.EvaluateAsync(raw.DataSourceId, purpose, now, snapshot.Manifest.MetadataManifest.Definition.Context, token);
                    if (!decision.Allowed || decision.Restrictions.Any(r => r.MaximumRetentionDays is { } days && now - raw.RetrievedAtUtc > TimeSpan.FromDays(days))) return verification with { CurrentlyAuthorized = false };
                }
                if (raw.ByteLength is not { } length) { hashes = false; continue; }
                await store.ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, length), token);
            }
            catch (FileNotFoundException) { available = false; hashes = false; }
            catch (DirectoryNotFoundException) { available = false; hashes = false; }
            catch (IOException) { hashes = false; }
            catch (InvalidDataException) { hashes = false; }
        }
        if (snapshot.Manifest.ResultGovernance is { } g) await governance.EnsureCurrentAsync(g, snapshot.Manifest, token);
        return verification with { RawAvailable = available, RawHashVerified = hashes };
    }
}
