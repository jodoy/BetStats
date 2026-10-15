using System.Text.Json;
using BetStats.Application.Coverage;
using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Football;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Pipeline;
using BetStats.Application.Providers;
using BetStats.Application.Quality;
using BetStats.Domain.Coverage;
using BetStats.Domain.Governance;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Pipeline;

public sealed record LocalAcquisitionInput(AcquisitionPlan Plan, string Path);
public sealed class FootballAcquisition(BetStatsDbContext db, FootballImportOperations imports, IFootballIngestionPersistence persistence,
    IRawPayloadStore store, ISourcePolicyEvaluator policies, ISourceOperationalStatus sources, IHistoricalCoverage coverage,
    IAnalyticalQualityGate gate, IFootballResults results, ProviderPublication publications)
{
    private async Task Authorize(AcquisitionCapability capability, InspectionAudience display, bool pin, CancellationToken token)
    {
        capability.Validate();
        if (await sources.ReadAsync(capability.SourceId, token) != SourceOperationalStatus.Enabled) throw new UnauthorizedAccessException("Acquisition source unavailable.");
        var now = await QualityPersistence.Now(db, token);
        foreach (var purpose in FootballProviderContract.ImportPurposes.Concat(display ? [display.Purpose] : Array.Empty<DataPurpose>()))
        {
            var decision = await policies.EvaluateAsync(capability.SourceId, purpose, now, display.Context, token);
            if (!decision.Allowed || pin && (decision.PolicyId != capability.PolicyId || decision.Version != capability.PolicyVersion))
                throw new UnauthorizedAccessException("Acquisition current policy denied or changed.");
        }
    }
    private async Task<byte[]> File(AcquisitionCapability capability, string path, CancellationToken token)
    {
        if (capability.Transport != FootballTransport.LocalCsv) throw new ArgumentException("Live provider transport disabled.");
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Absolute operator-supplied local file required.");
        await Authorize(capability, false, true, token);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (file.Length < 1 || file.Length > capability.MaximumBytes) throw new ArgumentException("Bounded acquisition file required.");
        var bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, token);
        if (await file.ReadAsync(new byte[1], token) != 0) throw new InvalidDataException("Acquisition file changed during read.");
        await Authorize(capability, false, true, token); return bytes;
    }
    public async Task<AcquisitionPlan> PlanAsync(Guid source, string path, FootballImportScope scope, AcquisitionCategory category,
        ValidationEvidenceKind evidenceKind, Guid? dateObservation, Guid? corrects, CancellationToken token)
    {
        if (!Enum.IsDefined(category) || !Enum.IsDefined(evidenceKind) || !scope.IsValid) throw new ArgumentException("Explicit category and scope required.");
        var decision = await policies.EvaluateAsync(source, DataPurpose.DataRetrieval, await QualityPersistence.Now(db, token), new(), token);
        if (!decision.Allowed || decision.PolicyId is not { } policy || decision.Version is not { } version) throw new UnauthorizedAccessException("Approved policy required.");
        var providerCode = await db.DataSources.AsNoTracking().Where(s => s.Id == source).Select(s => s.Code).SingleAsync(token);
        var capability = new AcquisitionCapability(1, source, providerCode, policy, version, FootballTransport.LocalCsv,
            category == AcquisitionCategory.KickoffEvidence ? "source-event-time-v1" : HistoricalFootballCsvParser.Version, [category]);
        var bytes = await File(capability, path, token);
        var plan = new AcquisitionPlan(1, capability, scope, category, CanonicalDatasetJson.Hash(bytes), bytes.Length, evidenceKind, dateObservation, corrects);
        plan.Validate(); return plan;
    }
    private async Task<AcquisitionRaw> Raw(Guid id, AcquisitionCapability capability, InspectionAudience display, bool pin, CancellationToken token)
    {
        await Authorize(capability, display, pin, token);
        var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == id, token);
        if (raw.DataSourceId != capability.SourceId || raw.ByteLength is not { } length) throw new InvalidDataException("Source-bound RAW manifest required.");
        var now = await QualityPersistence.Now(db, token);
        foreach (var purpose in new[] { DataPurpose.RawPayloadStorage, DataPurpose.HistoricalRetention, DataPurpose.InternalAnalytics }.Concat(display ? [display.Purpose] : Array.Empty<DataPurpose>()))
        {
            var p = await policies.EvaluateAsync(raw.DataSourceId, purpose, now, display.Context, token);
            if (!p.Allowed || p.Restrictions.Any(r => r.MaximumRetentionDays is { } days && now - raw.RetrievedAtUtc > TimeSpan.FromDays(days)))
                throw new UnauthorizedAccessException("Acquisition RAW retention denied.");
        }
        _ = await store.ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, length), token);
        return new(raw.Id, raw.ContentHashSha256, length, raw.RetrievedAtUtc, raw.RecordedAtUtc);
    }
    internal Task<AcquisitionRaw> EvidenceAsync(Guid id, AcquisitionCapability capability, InspectionAudience display, CancellationToken token) => Raw(id, capability, display, false, token);
    public async Task<AcquisitionReport> ExecuteAsync(Guid execution, LocalAcquisitionInput input, PipelineApproval approval, bool recover, CancellationToken token)
    {
        input.Plan.Validate(); approval.Validate();
        var plan = input.Plan; await Authorize(plan.Capability, false, true, token);
        var bytes = await File(plan.Capability, input.Path, token);
        if (CanonicalDatasetJson.Hash(bytes) != plan.Hash || bytes.Length != plan.Length) throw new InvalidDataException("Approved acquisition bytes changed.");
        var operation = PipelineOperationIds.Child(execution, "local-import");
        Guid rawId; string outcome;
        if (plan.Category == AcquisitionCategory.KickoffEvidence)
        {
            (rawId, outcome) = await Kickoff(execution, plan, bytes, approval, token);
        }
        else
        {
            var fingerprint = CanonicalDatasetJson.Fingerprint(new { Version = 1, SourceId = plan.Capability.SourceId, Scope = plan.Scope, Profile = plan.Capability.Profile, Hash = plan.Hash });
            var imported = await imports.RunAsync(operation, new(1, plan.Capability.SourceId, plan.Scope, plan.Capability.Profile, plan.Hash, plan.Length, fingerprint),
                input.Path, approval.Actor, approval.Reason, recover, token);
            if (imported.Outcome is not (ImportOutcome.Succeeded or ImportOutcome.Reused or ImportOutcome.Partial)) throw new IOException("Local acquisition did not complete.");
            var audit = await db.IngestionAuditEvents.AsNoTracking().Where(a => a.AttemptId == imported.AttemptId).OrderByDescending(a => a.Sequence).FirstAsync(token);
            rawId = await db.RawPayloads.AsNoTracking().Where(r => r.IngestionRunId == audit.RunId).Select(r => r.Id).SingleAsync(token);
            outcome = imported.Outcome.ToString();
        }
        var raw = await Raw(rawId, plan.Capability, false, true, token);
        if (raw.Hash != plan.Hash) throw new InvalidDataException("Acquisition RAW binding mismatch.");
        var original = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == rawId, token);
        var observed = new List<AcquisitionObservation>(); var evidenceIds = new HashSet<Guid> { rawId };
        var at = await QualityPersistence.Now(db, token);
        if (plan.Category == AcquisitionCategory.KickoffEvidence)
        {
            var receipt = await Receipt(plan.DateObservationId!.Value, plan, at, evidenceIds, token); observed.Add(receipt);
        }
        else
        {
            var parsed = new HistoricalFootballCsvParser().Parse(bytes, plan.Scope, token);
            if (parsed.ParsedCount > 500) throw new ArgumentException("Acquisition report supports 500 rows; no truncation.");
            var refs = parsed.Records.Select(r => r.MatchReference).ToArray();
            var anchors = await db.ProviderIdentities.AsNoTracking().Where(i => i.DataSourceId == plan.Capability.SourceId &&
                i.EntityKind == BetStats.Domain.Identity.CanonicalEntityKind.SportingEvent && refs.Contains(i.ExternalId)).ToArrayAsync(token);
            foreach (var anchor in anchors.OrderBy(a => a.Id))
            {
                var observation = await db.Observations.AsNoTracking().Where(o => o.ProviderIdentityId == anchor.Id && o.Type == ObservationType.EventDate &&
                    o.RecordedAtUtc <= at && o.AvailableAtUtc <= at).OrderByDescending(o => o.Version).FirstOrDefaultAsync(token);
                if (observation is not null) observed.Add(await Receipt(observation.Id, plan, at, evidenceIds, token));
            }
            if (parsed.Issues.Count > 0 || observed.Count != parsed.ParsedCount) outcome = "Partial";
        }
        var evidence = new List<AcquisitionRaw>(); foreach (var id in evidenceIds.Order()) evidence.Add(await Raw(id, plan.Capability, false, true, token));
        return new(1, plan.Fingerprint, plan.Capability, plan.Scope, plan.Category, plan.EvidenceKind,
            outcome == "Partial" || observed.Count == 0 || observed.Any(o => o.Exclusions.Count > 0) ? AcquisitionState.Partial : AcquisitionState.Verified,
            at, operation, outcome, await db.RawPayloads.AnyAsync(r => r.Id != rawId && r.DataSourceId == plan.Capability.SourceId && r.ContentHashSha256 == plan.Hash, token),
            raw, observed, ["independent_source_verification_unavailable", "coverage_not_established_by_capture", "prediction_pipeline_required",
                plan.EvidenceKind == ValidationEvidenceKind.Synthetic ? "synthetic_software_evidence" : "operator_origin_claim_not_independent_proof"], evidence);
    }
    private async Task<(Guid, string)> Kickoff(Guid execution, AcquisitionPlan plan, byte[] bytes, PipelineApproval approval, CancellationToken token)
    {
        await db.Database.OpenConnectionAsync(token); var locked = false;
        try
        {
            locked = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock(hashtextextended({plan.Capability.SourceId.ToString("D")},9017)) AS \"Value\"").SingleAsync(token);
            if (!locked) throw new InvalidOperationException("Acquisition source owner active.");
            var attemptId = PipelineOperationIds.Child(execution, "kickoff-raw");
            var audit = await db.IngestionAuditEvents.AsNoTracking().Where(a => a.AttemptId == attemptId).OrderByDescending(a => a.Sequence).FirstOrDefaultAsync(token);
            var attempt = audit is null ? await persistence.BeginAsync(attemptId, plan.Capability.SourceId, token) : new ImportReport(attemptId, audit.RunId, plan.Capability.SourceId, ImportOutcome.Running);
            var raw = audit is null ? null : await db.RawPayloads.AsNoTracking().SingleOrDefaultAsync(r => r.IngestionRunId == audit.RunId, token);
            if (raw is null)
            {
                var staged = await store.StageAsync(bytes, token); var retrieved = await QualityPersistence.Now(db, token);
                var captured = await persistence.CaptureAsync(attempt, new(bytes, "application/json", retrieved, "source-event-time-v1"), staged, plan.Scope, token);
                raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == captured.Id, token);
            }
            if (raw.ContentHashSha256 != plan.Hash) throw new InvalidDataException("Recovered RAW fingerprint mismatch.");
            await store.FinalizeAsync(new(raw.StorageKey, raw.ContentHashSha256, raw.ByteLength!.Value), token);
            if (audit?.Sequence != 2) await persistence.CompleteAsync(attempt with { Outcome = ImportOutcome.Succeeded, RetrievedPayloads = 1 }, token);
            // Exact original bytes are durable before any JSON interpretation.
            var claim = CanonicalDatasetJson.Deserialize<EventTimeSourceClaim>((await store.ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, raw.ByteLength!.Value), token)).ToArray());
            if (claim.Context != new FootballTimeContext(plan.Scope.CompetitionReference, plan.Scope.SeasonReference)) throw new InvalidDataException("Kickoff scope mismatch.");
            var existing = await db.EventTimeEvidence.AsNoTracking().SingleOrDefaultAsync(e => e.RawId == raw.Id, token);
            if (existing is null)
                _ = await coverage.RecordTimeAsync(new(plan.DateObservationId!.Value, raw.Id, claim.Value, "authorized-local-source-time-v1", plan.CorrectsKickoffId,
                    null, await QualityPersistence.Now(db, token), approval.Actor, approval.Reason), token);
            else if (existing.DateObservationId != plan.DateObservationId || existing.CorrectsId != plan.CorrectsKickoffId) throw new InvalidDataException("Recovered kickoff binding changed.");
            return (raw.Id, existing is null ? "Recorded" : "Reused");
        }
        finally { if (locked) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock(hashtextextended({plan.Capability.SourceId.ToString("D")},9017))", CancellationToken.None); await db.Database.CloseConnectionAsync(); }
    }
    private async Task<AcquisitionObservation> Receipt(Guid observationId, AcquisitionPlan plan, DateTime at, HashSet<Guid> rawIds, CancellationToken token)
    {
        var observation = await db.Observations.AsNoTracking().SingleAsync(o => o.Id == observationId && o.DataSourceId == plan.Capability.SourceId, token);
        if (observation.RawPayloadId is not { } rawId) throw new InvalidDataException("Observation RAW missing."); rawIds.Add(rawId);
        var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == rawId, token);
        if (await FootballContext.ReadAsync(db, raw, plan.Scope, token) != plan.Scope) throw new InvalidDataException("Original observation scope mismatch.");
        var quality = await gate.EvaluateAsync(new(observation.Id, at, DataPurpose.InternalAnalytics, new()), token);
        var exclusions = quality.Reasons.ToList();
        var eventId = quality.InterpretedTargetId ?? Guid.Empty;
        var sportingEvent = eventId == Guid.Empty ? null : await db.SportingEvents.AsNoTracking().Include(e => e.Participants).SingleAsync(e => e.Id == eventId, token);
        var times = await coverage.TimesAsync(observation.ProviderIdentityId, at, DatasetMode.HistoricalAsKnown, null, DataPurpose.InternalAnalytics, new(), token);
        var precise = times.Where(t => t.Resolution.UtcInstant is not null && t.Resolution.Precision is EventTimePrecision.Minute or EventTimePrecision.Second).ToArray();
        var time = precise.Length == 1 ? precise[0] : null;
        if (time is null) exclusions.Add("precise_kickoff_unavailable_or_conflicting"); else rawIds.Add(time.Evidence.RawId);
        var result = await db.FootballResults.AsNoTracking().Where(r => r.SourceId == plan.Capability.SourceId && r.EventId == eventId && r.RecordedAtUtc <= at && r.AvailableAtUtc <= at)
            .OrderByDescending(r => r.Version).FirstOrDefaultAsync(token);
        if (result is not null) rawIds.Add(result.RawId);
        var final = false;
        if (sportingEvent is not null)
        {
            var verified = await results.ReadAsync(new(sportingEvent.CompetitionId, sportingEvent.SeasonId ?? throw new InvalidDataException("Reviewed season required."), at, DataPurpose.InternalAnalytics, new(), eventId, plan.Capability.SourceId), token);
            final = verified.Results.Count == 1 && verified.Results[0].Eligible && verified.Results[0].Labels is not null;
            if (verified.Results.Any(r => r.Reasons.Any(reason => reason.Contains("conflict", StringComparison.Ordinal)))) exclusions.Add("result_conflicting");
        }
        return new(eventId, observation.Id, quality.DecisionId ?? Guid.Empty,
            sportingEvent?.Participants.SingleOrDefault(p => p.Role == BetStats.Domain.Sports.ParticipantRole.Home)?.ParticipantId ?? Guid.Empty,
            sportingEvent?.Participants.SingleOrDefault(p => p.Role == BetStats.Domain.Sports.ParticipantRole.Away)?.ParticipantId ?? Guid.Empty,
            sportingEvent?.CompetitionId ?? Guid.Empty, sportingEvent?.SeasonId ?? Guid.Empty, observation.DateValue!.Value,
            observation.AvailableAtUtc, observation.RecordedAtUtc, observation.SourcePublishedAtUtc,
            time?.Resolution.UtcInstant, time?.Resolution.Precision ?? EventTimePrecision.Unknown, time?.Evidence.Id,
            time?.Evidence.RecordedAtUtc, time?.Evidence.AvailableAtUtc, result?.Id, final,
            time?.Resolution.UtcInstant is { } instant ? observation.RecordedAtUtc >= instant : observation.DateValue.Value < DateOnly.FromDateTime(observation.RecordedAtUtc),
            exclusions.Distinct().Order(StringComparer.Ordinal).ToArray(), observation.SourceEventTimeUtc, result?.PublishedAtUtc, result?.AvailableAtUtc, result?.RecordedAtUtc, time?.Evidence.Value);
    }
    public async Task AuthorizePublicationAsync(AcquisitionReport report, CancellationToken token)
    {
        await QualityPersistence.Lock(db, report.Capability.SourceId, token);
        foreach (var raw in report.EvidenceRaws)
            if (await Raw(raw.Id, report.Capability, false, true, token) != raw) throw new InvalidDataException("Acquisition evidence changed.");
    }
    public async Task<AcquisitionJobStatus> StatusAsync(Guid id, InspectionAudience display, CancellationToken token)
    {
        var job = await db.Set<PipelineJob>().AsNoTracking().SingleAsync(j => j.Id == id, token);
        var version = await db.Set<PipelineJobVersion>().AsNoTracking().SingleAsync(v => v.JobId == id && v.Version == job.Version, token);
        if (version.Hash != CanonicalDatasetJson.Hash(version.Content)) throw new InvalidDataException("Job definition corrupt.");
        var definition = CanonicalDatasetJson.Deserialize<PipelineDefinition>(version.Content);
        if (definition.Kind == PipelineKind.ProviderPublication)
        {
            var status = await publications.StatusAsync(id, display, token);
            return new(id, status.State, job.Enabled, true, status.Attempts);
        }
        if (definition.Kind != PipelineKind.FootballAcquisition) throw new ArgumentException("Acquisition job required.");
        var input = JsonSerializer.Deserialize<LocalAcquisitionInput>(definition.PayloadJson, PipelineOperatorCommand.JsonOptions) ?? throw new InvalidDataException("Acquisition input missing.");
        input.Plan.Validate(); await Authorize(input.Plan.Capability, display, false, token);
        var executions = await db.Set<PipelineExecution>().AsNoTracking().Where(e => e.JobId == id).OrderByDescending(e => e.StartedUtc).ThenBy(e => e.Id).Take(100)
            .Select(e => new AcquisitionExecutionStatus(e.Id, e.State, e.Attempt, e.PlannedUtc, e.StartedUtc, e.CompletedUtc)).ToArrayAsync(token);
        return new(id, job.State, job.Enabled, true, executions);
    }
    public async Task<AcquisitionArtifact> ReadAsync(Guid execution, InspectionAudience display, CancellationToken token)
    {
        var providerVersion = await (from e in db.Set<PipelineExecution>() join v in db.Set<PipelineJobVersion>() on new { e.JobId, Version = e.DefinitionVersion } equals new { v.JobId, v.Version }
            where e.Id == execution select v).AsNoTracking().SingleAsync(token);
        if (providerVersion.Hash != CanonicalDatasetJson.Hash(providerVersion.Content)) throw new InvalidDataException("Acquisition definition corrupt.");
        if (CanonicalDatasetJson.Deserialize<PipelineDefinition>(providerVersion.Content).Kind == PipelineKind.ProviderPublication)
            return await publications.AcquisitionAsync(execution, display, token);
        var artifact = await db.Set<PipelineArtifact>().AsNoTracking().SingleAsync(a => a.ExecutionId == execution, token);
        if (artifact.Hash != CanonicalDatasetJson.Hash(artifact.Content) || !await db.Set<PipelineExecution>().AnyAsync(e => e.Id == execution && e.State == PipelineState.Completed, token))
            throw new InvalidDataException("Completed acquisition artifact required.");
        using var document = JsonDocument.Parse(artifact.Content); var result = document.RootElement.GetProperty("Result");
        if (result.GetProperty("Kind").GetString() != "football-acquisition-v1" || document.RootElement.GetProperty("ExecutionId").GetGuid() != execution)
            throw new InvalidDataException("Acquisition output binding mismatch.");
        var report = CanonicalDatasetJson.Deserialize<AcquisitionReport>(System.Text.Encoding.UTF8.GetBytes(result.GetProperty("Report").GetRawText()));
        var executionRow = await db.Set<PipelineExecution>().AsNoTracking().SingleAsync(e => e.Id == execution, token);
        var version = await db.Set<PipelineJobVersion>().AsNoTracking().SingleAsync(v => v.JobId == executionRow.JobId && v.Version == executionRow.DefinitionVersion, token);
        if (version.Hash != CanonicalDatasetJson.Hash(version.Content)) throw new InvalidDataException("Acquisition definition corrupt.");
        var definition = CanonicalDatasetJson.Deserialize<PipelineDefinition>(version.Content);
        var input = JsonSerializer.Deserialize<LocalAcquisitionInput>(definition.PayloadJson, PipelineOperatorCommand.JsonOptions) ?? throw new InvalidDataException("Acquisition input missing.");
        input.Plan.Validate();
        if (definition.Kind != PipelineKind.FootballAcquisition || report.Version != 1 || report.PlanHash != input.Plan.Fingerprint || report.Raw.Hash != input.Plan.Hash ||
            report.Scope != input.Plan.Scope || report.EvidenceRaws.Count is < 1 or > 1501) throw new InvalidDataException("Acquisition report input mismatch.");
        foreach (var raw in report.EvidenceRaws)
            if (await Raw(raw.Id, report.Capability, display, false, token) != raw) throw new InvalidDataException("Acquisition RAW mismatch.");
        return new(execution, artifact.Hash, report);
    }
}
