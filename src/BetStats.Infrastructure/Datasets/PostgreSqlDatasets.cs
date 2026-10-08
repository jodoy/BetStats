using System.Data;
using System.Text.Json;
using System.Text;
using BetStats.Application.Datasets;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Application.Quality;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Datasets;

public sealed class PostgreSqlDatasets(BetStatsDbContext db, IRawPayloadStore rawStore, IFootballMetadataParser parser,
    IAnalyticalQualityGate gate, ISourcePolicyEvaluator policies, ISourceOperationalStatus sources) : IDatasets
{
    private sealed class Denied(string code) : Exception(code);
    private static IEnumerable<DataPurpose> Purposes(DatasetDefinition d) => new[] { d.Purpose, DataPurpose.InternalAnalytics, DataPurpose.HistoricalRetention }.Distinct();

    public async Task<DatasetBuildResult> BuildAsync(DatasetBuildRequest request, CancellationToken token = default)
    {
        request.Definition.Validate(); QualityPersistence.Operator(request.OperatorId, request.Reason);
        var d = CanonicalDatasetJson.Normalize(request.Definition);
        if (d.SportId != FootballQualityRules.Football) throw new ArgumentException("Only football metadata schema v1 is supported.");
        var attempt = Guid.NewGuid(); var fingerprint = CanonicalDatasetJson.Fingerprint(d);
        await Append(attempt, 1, DatasetBuildStatus.Requested, fingerprint, request.OperatorId, request.Reason, null, null, token);
        try
        {
            await Append(attempt, 2, DatasetBuildStatus.Running, fingerprint, request.OperatorId, request.Reason, null, null, token);
            DatasetManifest manifest;
            await using (var assembly = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token))
            {
                var now = await QualityPersistence.Now(db, token);
                if ((d.ReconstructionAtUtc ?? d.AsOfUtc) > now) throw new Denied("future_knowledge_cutoff");
                manifest = await Assemble(d, fingerprint, token);
                await assembly.CommitAsync(token);
            }
            var bytes = CanonicalDatasetJson.Serialize(manifest);
            if (bytes.Length > 16 * 1024 * 1024) throw new Denied("artifact_bound_exceeded");
            var hash = CanonicalDatasetJson.Hash(bytes);
            await using var finalization = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            // Lock order: content -> attempt -> sorted sources. Policy/review writers use only sources.
            await LockKey(hash, token); await LockKey(attempt.ToString("D"), token);
            if (await db.DatasetBuildEvents.AnyAsync(e => e.AttemptId == attempt && e.Sequence == 3, token)) throw new Denied("attempt_already_closed");
            await Authorize(manifest, true, token);
            var artifact = await db.DatasetArtifacts.AsNoTracking().SingleOrDefaultAsync(a => a.ManifestHash == hash, token);
            if (artifact is not null && !artifact.Content.AsSpan().SequenceEqual(bytes)) throw new Denied("content_key_mismatch");
            if (artifact is null)
            {
                artifact = new() { Id = Guid.NewGuid(), ManifestHash = hash, DefinitionFingerprint = fingerprint, Content = bytes,
                    RowCount = manifest.Rows.Count, FeatureSchemaVersion = d.FeatureSchemaVersion, BuiltAtUtc = await QualityPersistence.Now(db, token) };
                db.DatasetArtifacts.Add(artifact);
                foreach (var row in manifest.Rows)
                    db.DatasetFeatures.Add(new() { Id = Guid.NewGuid(), DatasetId = artifact.Id, EventId = row.EventId,
                        PredictionCutoffUtc = row.PredictionCutoffUtc, Fingerprint = row.FeatureHash, Content = CanonicalDatasetJson.Serialize(new FeatureArtifact(1, row.Target, row.History, row.Features)) });
            }
            AddEvent(attempt, 3, DatasetBuildStatus.Succeeded, fingerprint, request.OperatorId, request.Reason, artifact.Id, null);
            await db.SaveChangesAsync(token); await finalization.CommitAsync(token);
            return new(attempt, DatasetBuildStatus.Succeeded, artifact.Id, hash, null);
        }
        catch (Exception error) when (error is Denied or IOException or InvalidDataException or DbUpdateException or System.Data.Common.DbException or OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            db.ChangeTracker.Clear();
            var status = error is OperationCanceledException ? DatasetBuildStatus.Cancelled : DatasetBuildStatus.Failed;
            var code = error is Denied ? error.Message : error is OperationCanceledException ? "cancelled" : error is IOException or InvalidDataException ? "raw_integrity_or_storage" : "assembly_or_persistence_failure";
            // If the DB is unavailable this may fail: the durable Running record then requires operator recovery.
            await using var terminal = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
            await LockKey(attempt.ToString("D"), CancellationToken.None);
            if (!await db.DatasetBuildEvents.AnyAsync(e => e.AttemptId == attempt && e.Sequence == 3))
                await Append(attempt, 3, status, fingerprint, request.OperatorId, request.Reason, null, code, CancellationToken.None);
            await terminal.CommitAsync(CancellationToken.None);
            return new(attempt, status, null, null, code);
        }
    }

    private async Task<DatasetManifest> Assemble(DatasetDefinition d, string fingerprint, CancellationToken token)
    {
        var rows = new List<DatasetRow>();
        foreach (var targetRequest in d.Targets)
        {
            var targetObservation = await db.Observations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == targetRequest.DateObservationId, token);
            if (targetObservation is null || targetObservation.Type != ObservationType.EventDate || targetObservation.DateValue is not { } date ||
                targetObservation.AvailableAtUtc > d.AsOfUtc || targetObservation.RecordedAtUtc > d.AsOfUtc)
                throw new Denied("target_evidence_missing_at_cutoff");
            var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            if (targetRequest.PredictionCutoffUtc >= midnight) throw new Denied("prediction_cutoff_not_before_target_day");
            // Target schedule/context is itself required at prediction time, never only at dataset AsOf.
            var target = await Evidence(targetObservation, d, targetRequest.PredictionCutoffUtc, token);
            if (target is null) throw new Denied("target_not_eligible_at_prediction_cutoff");
            var cutoff = targetRequest.PredictionCutoffUtc;
            var candidates = await db.Observations.AsNoTracking().Where(o => o.DataSourceId == target.SourceId && o.Type == ObservationType.EventDate &&
                o.AvailableAtUtc <= cutoff && o.RecordedAtUtc <= cutoff && o.DateValue >= d.SeasonStart && o.DateValue <= d.SeasonEnd &&
                !db.Observations.Any(n => n.ProviderIdentityId == o.ProviderIdentityId && n.Type == o.Type && n.Version > o.Version && n.AvailableAtUtc <= cutoff && n.RecordedAtUtc <= cutoff))
                .OrderBy(o => o.Id).Take(1001).ToListAsync(token);
            if (candidates.Count > 1000) throw new Denied("history_bound_exceeded");
            var history = new List<DatasetEvidenceReference>(); var excluded = new List<DatasetEligibilityFailure>();
            foreach (var candidate in candidates)
            {
                var evidence = await Evidence(candidate, d, cutoff, token);
                if (evidence is null) { excluded.Add(new(candidate.Id, "identity_scope_or_quality_denied")); continue; }
                if (evidence.EventId == target.EventId) { excluded.Add(new(candidate.Id, "target_event")); continue; }
                if (evidence.EventDate >= target.EventDate || evidence.EventDate >= DateOnly.FromDateTime(cutoff)) { excluded.Add(new(candidate.Id, "future_or_same_day")); continue; }
                if (evidence.Status == "Cancelled") { excluded.Add(new(candidate.Id, "cancelled")); continue; }
                history.Add(evidence);
            }
            // Independent providers agreeing on dates but differing participants are not silently collapsed.
            if (history.GroupBy(e => e.EventId).Any(g => g.Select(e => (e.HomeId, e.AwayId, e.EventDate, e.Status)).Distinct().Count() > 1))
                throw new Denied("conflicting_history_context");
            var ordered = history.OrderBy(e => e.EventId).ThenBy(e => e.DateObservationId).ToArray();
            var features = FootballMetadataFeatures.Compute(target, cutoff, ordered);
            rows.Add(new(target.EventId, cutoff, target, ordered, excluded.OrderBy(e => e.ObservationId).ToArray(), features, CanonicalDatasetJson.Fingerprint(new FeatureArtifact(1, target, ordered, features))));
        }
        if (rows.Select(r => (r.EventId, r.PredictionCutoffUtc)).Distinct().Count() != rows.Count) throw new Denied("duplicate_target");
        return new(1, 1, fingerprint, d, FootballQualityRules.Catalog.Select(r => r.Id + ":" + r.Version).Order(StringComparer.Ordinal).ToArray(),
            rows.OrderBy(r => r.EventId).ThenBy(r => r.PredictionCutoffUtc).ToArray());
    }

    private async Task<DatasetEvidenceReference?> Evidence(Observation o, DatasetDefinition d, DateTime cutoff, CancellationToken token)
    {
        var knowledge = d.ReconstructionAtUtc ?? cutoff;
        var dateGate = await gate.EvaluateAsync(new(o.Id, cutoff, d.Purpose, d.Context, d.Mode, d.ReconstructionAtUtc), token);
        if (!dateGate.Eligible || dateGate.InterpretedTargetId is not { } eventId) return null;
        var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == o.RawPayloadId, token);
        // Gate checks receipt time; authorize storage read too, before filesystem I/O.
        await AuthorizeSource(raw.DataSourceId, d, raw.RetrievedAtUtc, await QualityPersistence.Now(db, token), token);
        if (raw.ByteLength is not { } length) throw new Denied("raw_length_missing");
        var parsed = parser.Parse(await rawStore.ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, length), token), new(d.CompetitionReference, d.SeasonReference), token);
        var anchor = await db.ProviderIdentities.AsNoTracking().SingleAsync(i => i.Id == o.ProviderIdentityId, token);
        var row = parsed.Records.SingleOrDefault(r => r.MatchReference == anchor.ExternalId);
        if (row is null || row.MatchDate != o.DateValue) return null;
        var decisions = new List<IdentityResolution>(); var identities = new List<Guid>();
        foreach (var pair in new[] { (CanonicalEntityKind.Competition, "provider:competition:" + row.CompetitionReference),
            (CanonicalEntityKind.Season, FootballDataCsvParser.SeasonReference(row.CompetitionReference, row.SeasonReference)),
            (CanonicalEntityKind.Participant, row.HomeReference), (CanonicalEntityKind.Participant, row.AwayReference) })
        {
            var identity = await QualityPersistence.Anchor(db, o.DataSourceId, pair.Item1, pair.Item2, token);
            var decision = await QualityPersistence.Decision(db, identity?.Id, knowledge, token);
            if (identity is null || decision?.Status != ResolutionStatus.Resolved || decision.CanonicalId is null) return null;
            identities.Add(identity.Id); decisions.Add(decision);
        }
        if (decisions[0].CanonicalId != d.CompetitionId || decisions[1].CanonicalId != d.SeasonId || row.MatchDate < d.SeasonStart || row.MatchDate > d.SeasonEnd) return null;
        var status = await db.Observations.AsNoTracking().Where(s => s.ProviderIdentityId == o.ProviderIdentityId && s.Type == ObservationType.EventStatus &&
            s.AvailableAtUtc <= cutoff && s.RecordedAtUtc <= cutoff).OrderByDescending(s => s.Version).FirstOrDefaultAsync(token);
        var assessmentIds = dateGate.AssessmentIds.ToList(); string? statusValue = null;
        if (status is not null)
        {
            var statusGate = await gate.EvaluateAsync(new(status.Id, cutoff, d.Purpose, d.Context, d.Mode, d.ReconstructionAtUtc), token);
            if (!statusGate.Eligible || statusGate.InterpretedTargetId != eventId) return null;
            assessmentIds.AddRange(statusGate.AssessmentIds); statusValue = status.StatusValue?.ToString();
        }
        var policyRefs = new List<DatasetPolicyReference>();
        foreach (var purpose in Purposes(d))
        {
            var evaluation = await policies.EvaluateAsync(o.DataSourceId, purpose, knowledge, d.Context, token);
            if (!evaluation.Allowed || evaluation.PolicyId is not { } policyId) throw new Denied("historical_policy_denied");
            var audits = await db.PolicyAudits.AsNoTracking().Where(a => a.SourcePolicyId == policyId && a.RecordedAtUtc <= knowledge && a.ReviewedAtUtc <= knowledge)
                .OrderBy(a => a.Sequence).Select(a => a.Id).ToListAsync(token);
            policyRefs.Add(new(policyId, purpose, evaluation.Version!.Value, audits));
        }
        var frozen = new List<DatasetFrozenRecord>();
        void Freeze<T>(string kind, Guid id, T value) => frozen.Add(new(kind, id, Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(value))));
        Freeze("observation", o.Id, o); if (status is not null) Freeze("observation", status.Id, status);
        Freeze("raw", raw.Id, raw); Freeze("identity", anchor.Id, anchor);
        if (status?.RawPayloadId is { } statusRawId && statusRawId != raw.Id)
            Freeze("raw", statusRawId, await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == statusRawId, token));
        foreach (var id in identities) Freeze("identity", id, await db.ProviderIdentities.AsNoTracking().SingleAsync(i => i.Id == id, token));
        var eventDecision = await db.IdentityResolutions.AsNoTracking().SingleAsync(i => i.Id == dateGate.DecisionId, token);
        foreach (var decision in decisions.Append(eventDecision)) Freeze("decision", decision.Id, decision);
        foreach (var id in assessmentIds.Distinct()) Freeze("quality", id, await db.QualityAssessments.AsNoTracking().SingleAsync(a => a.Id == id, token));
        foreach (var policyId in policyRefs.Select(p => p.PolicyId).Distinct())
        {
            var policy = await db.SourcePolicies.AsNoTracking().Include(p => p.Permissions).SingleAsync(p => p.Id == policyId, token);
            Freeze("policy", policyId, new { policy.Id, policy.DataSourceId, policy.Version, policy.EffectiveFromUtc, policy.EffectiveToUtc,
                policy.TermsReference, policy.EvidenceReference, policy.RecordedAtUtc, Permissions = policy.Permissions.OrderBy(p => p.Purpose).ToArray() });
        }
        foreach (var auditId in policyRefs.SelectMany(p => p.AuditIds).Distinct()) Freeze("policy-audit", auditId, await db.PolicyAudits.AsNoTracking().SingleAsync(a => a.Id == auditId, token));
        return new(o.DataSourceId, eventId, decisions[2].CanonicalId!.Value, decisions[3].CanonicalId!.Value, o.ProviderIdentityId,
            identities.Order().ToArray(), decisions.Select(s => s.Id).Append(dateGate.DecisionId!.Value).Distinct().Order().ToArray(),
            o.Id, status?.Id, raw.Id, raw.ContentHashSha256, raw.RecordedAtUtc, o.AvailableAtUtc, o.RecordedAtUtc, row.MatchDate, statusValue,
            assessmentIds.Distinct().Order().ToArray(), policyRefs.OrderBy(p => p.Purpose).ToArray(), cutoff, knowledge,
            frozen.OrderBy(f => f.Kind, StringComparer.Ordinal).ThenBy(f => f.Id).ToArray());
    }

    private static DatasetEvidenceReference[] Evidence(DatasetManifest manifest) => manifest.Rows.SelectMany(r => r.History.Append(r.Target)).ToArray();
    private async Task AuthorizeSource(Guid source, DatasetDefinition d, DateTime retrieved, DateTime now, CancellationToken token)
    {
        if (await sources.ReadAsync(source, token) != SourceOperationalStatus.Enabled) throw new Denied("source_not_enabled");
        foreach (var purpose in Purposes(d))
        {
            var evaluation = await policies.EvaluateAsync(source, purpose, now, d.Context, token);
            if (!evaluation.Allowed) throw new Denied("current_policy_denied");
            if (evaluation.Restrictions.Any(r => r.MaximumRetentionDays is { } days && now - retrieved > TimeSpan.FromDays(days))) throw new Denied("retention_age_exceeded");
        }
    }
    private async Task Authorize(DatasetManifest manifest, bool locks, CancellationToken token)
    {
        var evidence = Evidence(manifest);
        foreach (var source in evidence.Select(e => e.SourceId).Distinct().Order())
        {
            if (locks) await QualityPersistence.Lock(db, source, token);
            var rawIds = evidence.Where(e => e.SourceId == source).SelectMany(e => e.FrozenRecords.Where(f => f.Kind == "raw").Select(f => f.Id)).Distinct().ToArray();
            var earliest = await db.RawPayloads.Where(r => rawIds.Contains(r.Id)).MinAsync(r => r.RetrievedAtUtc, token);
            await AuthorizeSource(source, manifest.Definition, earliest, await QualityPersistence.Now(db, token), token);
        }
    }
    private Task LockKey(string key, CancellationToken token) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 7007))", token);
    private void AddEvent(Guid attempt, int sequence, DatasetBuildStatus status, string fingerprint, string actor, string reason, Guid? snapshot, string? failure) =>
        db.DatasetBuildEvents.Add(new() { Id = Guid.NewGuid(), AttemptId = attempt, Sequence = sequence, Status = status, DefinitionFingerprint = fingerprint,
            OperatorId = actor, Reason = reason, SnapshotId = snapshot, FailureCode = failure });
    private async Task Append(Guid attempt, int sequence, DatasetBuildStatus status, string fingerprint, string actor, string reason, Guid? snapshot, string? failure, CancellationToken token)
    { AddEvent(attempt, sequence, status, fingerprint, actor, reason, snapshot, failure); await db.SaveChangesAsync(token); }

    public async Task MarkInterruptedAsync(Guid attemptId, string actor, string reason, CancellationToken token = default)
    {
        QualityPersistence.Operator(actor, reason);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await LockKey(attemptId.ToString("D"), token);
        var latest = await db.DatasetBuildEvents.Where(e => e.AttemptId == attemptId).OrderByDescending(e => e.Sequence).FirstOrDefaultAsync(token)
            ?? throw new ArgumentException("Attempt not found.");
        if (latest.Sequence != 3) await Append(attemptId, 3, DatasetBuildStatus.Failed, latest.DefinitionFingerprint, actor, reason, null, "owner_confirmed_interrupted", token);
        await tx.CommitAsync(token);
    }
    private async Task<(DatasetArtifact Artifact, DatasetManifest Manifest)> Load(Guid id, CancellationToken token)
    {
        if (id == Guid.Empty) throw new ArgumentException("Snapshot ID required.");
        var artifact = await db.DatasetArtifacts.AsNoTracking().SingleAsync(a => a.Id == id, token);
        return (artifact, CanonicalDatasetJson.Deserialize<DatasetManifest>(artifact.Content));
    }
    public async Task<DatasetSnapshot> InspectAsync(Guid id, CancellationToken token = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var (artifact, manifest) = await Load(id, token);
        await Authorize(manifest, true, token);
        if (CanonicalDatasetJson.Hash(artifact.Content) != artifact.ManifestHash) throw new InvalidDataException("Artifact hash mismatch.");
        await tx.CommitAsync(token);
        return new(id, artifact.ManifestHash, artifact.BuiltAtUtc, artifact.RecordedAtUtc, manifest);
    }
    public async Task<DatasetVerification> VerifyAsync(Guid id, CancellationToken token = default)
    {
        var reasons = new List<string>();
        DatasetArtifact artifact; DatasetManifest manifest;
        try { (artifact, manifest) = await Load(id, token); }
        catch (Exception e) when (e is JsonException or InvalidDataException or FormatException) { return new(id, false, false, false, false, ["artifact_unreadable"]); }
        var integrity = CanonicalDatasetJson.Hash(artifact.Content) == artifact.ManifestHash && CanonicalDatasetJson.Serialize(manifest).AsSpan().SequenceEqual(artifact.Content) &&
            manifest.ManifestVersion == 1 && manifest.SerializerVersion == 1 && manifest.DefinitionFingerprint == artifact.DefinitionFingerprint &&
            CanonicalDatasetJson.Fingerprint(manifest.Definition) == manifest.DefinitionFingerprint && manifest.Rows.Count == artifact.RowCount && artifact.FeatureSchemaVersion == 1;
        if (!integrity) reasons.Add("artifact_or_manifest_integrity");
        var permission = true;
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token))
        {
            try { await Authorize(manifest, true, token); }
            catch (Denied) { permission = false; reasons.Add("current_permission_denied"); }
            await tx.CommitAsync(token);
        }
        var complete = permission; var reproducible = integrity;
        var featureRecords = await db.DatasetFeatures.AsNoTracking().Where(f => f.DatasetId == id).OrderBy(f => f.EventId).ThenBy(f => f.PredictionCutoffUtc).ToListAsync(token);
        if (featureRecords.Count != manifest.Rows.Count) { complete = false; reproducible = false; }
        foreach (var row in manifest.Rows)
        {
            var stored = featureRecords.SingleOrDefault(f => f.EventId == row.EventId && f.PredictionCutoffUtc == row.PredictionCutoffUtc);
            var calculated = FootballMetadataFeatures.Compute(row.Target, row.PredictionCutoffUtc, row.History);
            if (stored is null || row.FeatureHash != CanonicalDatasetJson.Fingerprint(new FeatureArtifact(1, row.Target, row.History, calculated)) ||
                row.FeatureHash != CanonicalDatasetJson.Fingerprint(new FeatureArtifact(1, row.Target, row.History, row.Features)) ||
                stored.Fingerprint != row.FeatureHash || CanonicalDatasetJson.Hash(stored.Content) != row.FeatureHash) reproducible = false;
        }
        if (permission)
            foreach (var e in Evidence(manifest).DistinctBy(e => e.DateObservationId))
            {
                var observation = await db.Observations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == e.DateObservationId, token);
                var raw = await db.RawPayloads.AsNoTracking().SingleOrDefaultAsync(r => r.Id == e.RawId, token);
                if (observation is null || raw is null || raw.ContentHashSha256 != e.RawHash || observation.RawPayloadId != e.RawId ||
                    observation.DateValue != e.EventDate || raw.RecordedAtUtc != e.RawRecordedUtc ||
                    await db.IdentityResolutions.CountAsync(d => e.DecisionIds.Contains(d.Id), token) != e.DecisionIds.Count ||
                    await db.ProviderIdentities.CountAsync(i => e.ContextIdentityIds.Contains(i.Id), token) != e.ContextIdentityIds.Count ||
                    await db.QualityAssessments.CountAsync(a => e.QualityAssessmentIds.Contains(a.Id), token) != e.QualityAssessmentIds.Count ||
                    e.StatusObservationId is { } statusId && !await db.Observations.AnyAsync(o => o.Id == statusId && o.Type == ObservationType.EventStatus, token)) complete = false;
                foreach (var policy in e.Policies)
                    if (!await db.SourcePolicies.AnyAsync(p => p.Id == policy.PolicyId && p.DataSourceId == e.SourceId, token) ||
                        await db.PolicyAudits.CountAsync(a => policy.AuditIds.Contains(a.Id), token) != policy.AuditIds.Count) complete = false;
            }
        if (!complete) reasons.Add(permission ? "evidence_incomplete" : "evidence_not_inspected_without_permission");
        if (!reproducible) reasons.Add("feature_reproduction_failed");
        return new(id, integrity, complete, permission, reproducible, reasons);
    }

    public async Task<DatasetComparison> CompareAsync(Guid left, Guid right, int offset, int limit, CancellationToken token = default)
    {
        if (offset is < 0 or > 100000 || limit is < 1 or > 200) throw new ArgumentException("Bounded comparison page required.");
        var a = await InspectAsync(left, token); var b = await InspectAsync(right, token);
        var differences = new List<DatasetDifference>();
        static string Summarize(JsonElement value) { var text = value.GetRawText(); return text.Length <= 500 ? text : "sha256:" + CanonicalDatasetJson.Hash(Encoding.UTF8.GetBytes(text)) + ";characters:" + text.Length; }
        void Compare(string path, JsonElement x, JsonElement y)
        {
            if (differences.Count > 100000) throw new InvalidOperationException("Comparison difference bound exceeded.");
            if (x.ValueKind == JsonValueKind.Object && y.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in x.EnumerateObject().Select(p => p.Name).Concat(y.EnumerateObject().Select(p => p.Name)).Distinct().Order(StringComparer.Ordinal))
                {
                    var hasX = x.TryGetProperty(key, out var vx); var hasY = y.TryGetProperty(key, out var vy);
                    if (hasX && hasY) Compare(path + "." + key, vx, vy); else differences.Add(new(path + "." + key, hasX ? Summarize(vx) : null, hasY ? Summarize(vy) : null));
                }
            }
            else if (x.ValueKind == JsonValueKind.Array && y.ValueKind == JsonValueKind.Array)
            {
                var xs = x.EnumerateArray().ToArray(); var ys = y.EnumerateArray().ToArray();
                for (var i = 0; i < Math.Max(xs.Length, ys.Length); i++)
                    if (i < xs.Length && i < ys.Length) Compare(path + "[" + i + "]", xs[i], ys[i]);
                    else differences.Add(new(path + "[" + i + "]", i < xs.Length ? Summarize(xs[i]) : null, i < ys.Length ? Summarize(ys[i]) : null));
            }
            else if (x.GetRawText() != y.GetRawText()) differences.Add(new(path, Summarize(x), Summarize(y)));
        }
        using var x = JsonDocument.Parse(CanonicalDatasetJson.Serialize(a.Manifest)); using var y = JsonDocument.Parse(CanonicalDatasetJson.Serialize(b.Manifest));
        Compare("manifest", x.RootElement, y.RootElement);
        return new(left, right, offset, differences.Count, differences.Skip(offset).Take(limit).ToArray());
    }
}
