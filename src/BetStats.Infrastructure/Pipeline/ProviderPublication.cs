using System.Text.Json;
using BetStats.Application.Datasets;
using BetStats.Application.Governance;
using BetStats.Application.Football;
using BetStats.Application.Ingestion;
using BetStats.Application.Pipeline;
using BetStats.Application.Providers;
using BetStats.Application.Quality;
using BetStats.Domain.Coverage;
using BetStats.Domain.Football;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Pipeline;

public sealed class ProviderPublication(BetStatsDbContext db, IRawPayloadStore store,
    ISourcePolicyEvaluator policies, ISourceOperationalStatus sources, PostgreSqlPipeline jobs,
    IAnalyticalQualityGate gate, IFootballResults footballResults) : IProviderPublicationReports
{
    private async Task<RawPayload> Raw(ProviderPublicationInput input, bool display, CancellationToken token)
    {
        if (input.Version != 1 || input.RawId == Guid.Empty || input.SourceId == Guid.Empty ||
            input.PolicyId == Guid.Empty || input.PolicyVersion < 1 || input.Profile != ApiFootballFixtureAdapter.Profile ||
            input.Scope is null || !input.Scope.IsValid || input.Canary is null || !input.Canary.IsValid ||
            !Enum.IsDefined(input.EvidenceKind) || string.IsNullOrWhiteSpace(input.SourceCode) || input.SourceCode.Length > 100)
            throw new ArgumentException("Supported source-bound original RAW publication plan required.");
        var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == input.RawId, token);
        if (raw.DataSourceId != input.SourceId || raw.ContentHashSha256 != input.RawHash ||
            raw.ExternalReference != "fixture:" + input.Profile || raw.ContentType != "application/json" || raw.ByteLength is not (> 0 and <= 1048576) ||
            await FootballContext.ReadAsync(db, raw, input.Scope, token) != input.Scope ||
            !await db.DataSources.AnyAsync(s => s.Id == input.SourceId && s.Code == input.SourceCode, token))
            throw new InvalidDataException("Original provider RAW manifest mismatch.");
        if (await sources.ReadAsync(input.SourceId, token) != SourceOperationalStatus.Enabled)
            throw new UnauthorizedAccessException("Provider source disabled.");
        var now = await QualityPersistence.Now(db, token);
        foreach (var at in new[] { raw.RecordedAtUtc, now })
        foreach (var purpose in FootballProviderContract.ImportPurposes.Concat(display ? [DataPurpose.PublicDisplay] : Array.Empty<DataPurpose>()))
        {
            var p = await policies.EvaluateAsync(input.SourceId, purpose, at, new(PublicDisplay: display, IntendedRetentionDays: 1), token);
            if (!p.Allowed || p.PolicyId != input.PolicyId || p.Version != input.PolicyVersion ||
                // Durable observations have no approved expiry/purge lifecycle in
                // this release. Never pretend a requested one-day use supplies one.
                p.Restrictions.Any(r => r.MaximumRetentionDays is not null))
                throw new UnauthorizedAccessException("Original or current publication rights denied.");
        }
        return raw;
    }
    private static ApiFootballPublicationContext Context(ProviderPublicationInput input, RawPayload raw) =>
        new(1, input.SourceId, "api-football", input.Profile, input.RawId, input.RawHash, input.Scope,
            input.PolicyId, input.PolicyVersion, raw.RetrievedAtUtc, raw.RecordedAtUtc, input.Canary);
    private async Task<IReadOnlyList<ApiFootballMappedFixture>> Map(ProviderPublicationInput input, RawPayload raw, CancellationToken token) =>
        ApiFootballPublicationMapping.Map(await store.ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, raw.ByteLength!.Value), token), Context(input, raw), token);

    public async Task<ProviderPublicationInput> PlanAsync(Guid rawId, ApiFootballCanary canary, ValidationEvidenceKind evidenceKind, CancellationToken token = default)
    {
        var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == rawId, token);
        var scope = await FootballContext.ReadAsync(db, raw, new(canary.League.ToString(System.Globalization.CultureInfo.InvariantCulture), canary.Season.ToString(System.Globalization.CultureInfo.InvariantCulture)), token);
        var policy = await policies.EvaluateAsync(raw.DataSourceId, DataPurpose.InternalAnalytics, await QualityPersistence.Now(db, token), new(IntendedRetentionDays: 1), token);
        if (!policy.Allowed || policy.PolicyId is not { } id || policy.Version is not { } version) throw new UnauthorizedAccessException("Approved policy required.");
        var sourceCode = await db.DataSources.Where(s => s.Id == raw.DataSourceId).Select(s => s.Code).SingleAsync(token);
        var input = new ProviderPublicationInput(1, raw.Id, raw.DataSourceId, sourceCode, raw.ContentHashSha256, id, version, scope, canary, evidenceKind);
        _ = await Map(input, await Raw(input, false, token), token); return input;
    }

    public async Task<ProviderPublicationArtifact> PublishAsync(PipelineClaim claim, ProviderPublicationInput input, PipelineApproval approval, CancellationToken token = default)
    {
        approval.Validate();
        if (claim.Definition.Kind != PipelineKind.ProviderPublication ||
            CanonicalDatasetJson.Fingerprint(JsonSerializer.Deserialize<ProviderPublicationInput>(claim.Definition.PayloadJson, PipelineOperatorCommand.JsonOptions)) != CanonicalDatasetJson.Fingerprint(input))
            throw new InvalidDataException("Publication input differs from claimed job.");
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(token);
        // Same order as pipeline completion/cancellation. These locks remain held until commit.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM pipeline.\"Jobs\" WHERE \"Id\"={claim.JobId} FOR UPDATE", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM pipeline.\"Executions\" WHERE \"Id\"={claim.ExecutionId} FOR UPDATE", token);
        await jobs.CheckAsync(claim, token);
        await QualityPersistence.Lock(db, input.SourceId, token);
        var raw = await Raw(input, false, token); var mapped = await Map(input, raw, token);
        var now = await QualityPersistence.Now(db, token);
        var bindings = new Dictionary<string, (ProviderIdentity Identity, IdentityResolution Decision)>();
        foreach (var row in mapped)
        foreach (var pair in References(row))
        {
            if (bindings.ContainsKey(pair.Reference)) continue;
            var anchor = await db.ProviderIdentities.SingleOrDefaultAsync(i => i.DataSourceId == input.SourceId && i.EntityKind == pair.Kind && i.ExternalId == pair.Reference, token);
            if (anchor is null) { anchor = new(Guid.NewGuid(), input.SourceId, pair.Kind, pair.Reference, now); db.Add(anchor); }
            var decision = await db.IdentityResolutions.Where(d => d.ProviderIdentityId == anchor.Id).OrderByDescending(d => d.Version).FirstOrDefaultAsync(token);
            if (decision is null) { decision = new(Guid.NewGuid(), anchor, ResolutionStatus.Unresolved, null, "ingestion:provider-review-required", "Explicit canonical provider binding required", now, rawPayloadId: raw.Id); db.Add(decision); }
            bindings.Add(pair.Reference, (anchor, decision));
        }
        var key = CanonicalDatasetJson.Fingerprint(new { Input = input, Mapping = ApiFootballPublicationMapping.Version,
            Bindings = bindings.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => new { b.Key, b.Value.Decision.Id, b.Value.Decision.Version, b.Value.Decision.CanonicalId }).ToArray() });
        var previousReceipt = await db.Set<ProviderPublicationRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.SourceId == input.SourceId && r.Key == key, token);
        if (previousReceipt is not null)
        {
            await jobs.CheckAsync(claim, token); await tx.CommitAsync(token); await tx.DisposeAsync();
            return await VerifyAsync(previousReceipt.Id, false, token);
        }
        var fixtures = new List<PublishedFixture>();
        var aliases = mapped.GroupBy(r => bindings[r.EventReference].Decision.CanonicalSportingEventId)
            .Where(g => g.Key is not null && g.Count() > 1).Select(g => g.Key!.Value).ToHashSet();
        foreach (var row in mapped)
        {
            token.ThrowIfCancellationRequested();
            var selected = References(row).Select(p => bindings[p.Reference]).ToArray();
            var decisions = selected.Select(p => new PublicationBinding(p.Identity.Id, p.Decision.Id, p.Decision.Version, p.Decision.CanonicalId, p.Identity.ExternalId)).ToArray();
            var exclusions = row.MetadataExclusions.ToList();
            if (selected.Any(p => p.Decision.Status != ResolutionStatus.Resolved || p.Decision.DecidedBy.StartsWith("ingestion:", StringComparison.Ordinal))) exclusions.Add("identity_review_required");
            Guid? eventId = selected[4].Decision.CanonicalSportingEventId;
            if (eventId is { } alias && aliases.Contains(alias)) exclusions.Add("canonical_fixture_alias_conflict");
            if (exclusions.Count > 0) { fixtures.Add(new(row.EventReference, eventId, decisions, null, null, null, null, null, row.ProviderStatus, exclusions, KickoffUtc: row.KickoffUtc)); continue; }
            var competition = await db.Competitions.AsNoTracking().SingleAsync(c => c.Id == selected[0].Decision.CanonicalCompetitionId, token);
            var season = await db.Seasons.AsNoTracking().SingleAsync(s => s.Id == selected[1].Decision.CanonicalSeasonId, token);
            var home = await db.Participants.AsNoTracking().SingleAsync(p => p.Id == selected[2].Decision.CanonicalParticipantId, token);
            var away = await db.Participants.AsNoTracking().SingleAsync(p => p.Id == selected[3].Decision.CanonicalParticipantId, token);
            var sportingEvent = await db.SportingEvents.AsNoTracking().Include(e => e.Participants).SingleAsync(e => e.Id == eventId, token);
            var date = DateOnly.FromDateTime(row.KickoffUtc!.Value);
            var record = new FootballMatchRecord(fixtures.Count + 2, input.Scope.CompetitionReference, input.Scope.SeasonReference, date,
                row.HomeReference, row.AwayReference, row.HomeReference, row.AwayReference, row.EventReference, false, row.Status,
                row.FinalResult is { } value ? new(value, null) : null);
            var assessment = await QualityPersistence.Assess(db, raw, record, now, token);
            var lastDate = await db.Observations.AsNoTracking().Where(o => o.ProviderIdentityId == selected[4].Identity.Id && o.Type == ObservationType.EventDate)
                .OrderByDescending(o => o.Version).FirstOrDefaultAsync(token);
            // A reviewed target correction is a new forward observation. Never rebind
            // old RAW to a different target or alter an earlier observation's target.
            if (lastDate?.CanonicalSportingEventId is { } oldTarget && oldTarget != eventId && raw.RetrievedAtUtc > lastDate.RetrievedAtUtc &&
                await db.IdentityResolutions.AnyAsync(d => d.ProviderIdentityId == selected[4].Identity.Id && d.CanonicalSportingEventId == oldTarget && d.Version < selected[4].Decision.Version, token))
                assessment = (assessment.Identity, assessment.Issues.Select(i => i.Rule.Id == "football.replay"
                    ? new BetStats.Domain.Quality.QualityIssue(i.Rule, true, BetStats.Domain.Quality.QualitySeverity.Info, false, "reviewed_forward_identity_correction", BetStats.Domain.Quality.QualityClassification.HistoricalCorrection) : i).ToArray());
            if (competition.SportId != FootballQualityRules.Football || season.CompetitionId != competition.Id ||
                home.SportId != competition.SportId || away.SportId != competition.SportId || home.ParticipantType != ParticipantType.Team || away.ParticipantType != ParticipantType.Team ||
                home.Id == away.Id || sportingEvent.CompetitionId != competition.Id || sportingEvent.SeasonId != season.Id || sportingEvent.SportId != competition.SportId ||
                !sportingEvent.Participants.Any(p => p.ParticipantId == home.Id && p.Role == ParticipantRole.Home) ||
                !sportingEvent.Participants.Any(p => p.ParticipantId == away.Id && p.Role == ParticipantRole.Away)) exclusions.Add("canonical_binding_conflict");
            exclusions.AddRange(assessment.Issues.Where(i => i.BlocksEligibility).Select(i => i.ReasonCode));
            if (exclusions.Count > 0) { fixtures.Add(new(row.EventReference, eventId, decisions, null, null, null, null, null, row.ProviderStatus, exclusions.Distinct().ToArray(), KickoffUtc: row.KickoffUtc)); continue; }
            var anchor = selected[4].Identity;
            var dateObservation = await Fact(ObservationType.EventDate, date, null, null);
            var scheduleObservation = await Fact(ObservationType.ScheduledStart, null, row.KickoffUtc, null);
            var statusObservation = await Fact(ObservationType.EventStatus, null, null, row.Status);
            var previousTime = await db.EventTimeEvidence.Where(e => e.ProviderIdentityId == anchor.Id).OrderByDescending(e => e.Version).FirstOrDefaultAsync(token);
            EventTimeEvidence time;
            if (previousTime is not null && previousTime.RawId == raw.Id && previousTime.DateObservationId == dateObservation.Id) time = previousTime;
            else
            {
                time = new() { Id = Guid.NewGuid(), SourceId = input.SourceId, ProviderIdentityId = anchor.Id, DateObservationId = dateObservation.Id,
                    RawId = raw.Id, RawHash = raw.ContentHashSha256, PolicyId = input.PolicyId,
                    Value = new(date, TimeOnly.FromDateTime(row.KickoffUtc.Value), "UTC", 0, row.KickoffUtc, EventTimePrecision.Second),
                    EvidenceReference = "api-football-publication-mapping-v1", CorrectsId = previousTime?.Id, Version = (previousTime?.Version ?? 0) + 1,
                    RetrievedAtUtc = raw.RetrievedAtUtc, AvailableAtUtc = now, OperatorId = approval.Actor, Reason = approval.Reason[..Math.Min(500, approval.Reason.Length)] };
                db.Add(time);
            }
            FootballResultObservation? result = null;
            if (row.FinalResult is { } final)
            {
                var old = await db.FootballResults.Where(r => r.ProviderIdentityId == anchor.Id).OrderByDescending(r => r.Version).FirstOrDefaultAsync(token);
                var resultIssues = FootballResultRules.Assess(final, old?.Value, old is null || raw.RetrievedAtUtc > old.RetrievedAtUtc,
                    old is not null && raw.RetrievedAtUtc == old.RetrievedAtUtc && raw.Id != old.RawId);
                QualityPersistence.Record(db, claim.ExecutionId, raw, raw.IngestionRunId, record.Row, row.EventReference, anchor.Id, input.PolicyId, now, resultIssues);
                if (old is not null && old.RawId == raw.Id && old.EventId == eventId && old.Value == final) result = old;
                else if (resultIssues.Any(i => i.BlocksEligibility)) exclusions.AddRange(resultIssues.Where(i => i.BlocksEligibility).Select(i => i.ReasonCode));
                else if (old is not null && raw.RetrievedAtUtc <= old.RetrievedAtUtc) exclusions.Add("result_not_later_than_previous");
                else
                {
                    result = new() { Id = Guid.NewGuid(), SourceId = input.SourceId, RawId = raw.Id, ProviderIdentityId = anchor.Id, EventId = eventId!.Value,
                        CompetitionId = competition.Id, SeasonId = season.Id, HomeId = home.Id, AwayId = away.Id, DateObservationId = dateObservation.Id,
                        SourceEventReference = row.EventReference, CompetitionReference = input.Scope.CompetitionReference, SeasonReference = input.Scope.SeasonReference,
                        EventDate = date, Value = final, Version = (old?.Version ?? 0) + 1, CorrectsId = old?.Id, RetrievedAtUtc = raw.RetrievedAtUtc, AvailableAtUtc = now };
                    db.Add(result);
                }
            }
            QualityPersistence.Record(db, claim.ExecutionId, raw, raw.IngestionRunId, record.Row, row.EventReference, anchor.Id, input.PolicyId, now, assessment.Issues);
            fixtures.Add(new(row.EventReference, eventId, decisions, dateObservation.Id, scheduleObservation.Id, statusObservation.Id, time.Id,
                result?.Id, row.ProviderStatus, exclusions.Concat(row.ResultExclusions).Distinct().ToArray(), row.KickoffUtc, null,
                scheduleObservation.CorrectsObservationId, result?.CorrectsId, statusObservation.CorrectsObservationId));

            async Task<Observation> Fact(ObservationType type, DateOnly? day, DateTime? kickoff, SportingEventStatus? status)
            {
                var old = await db.Observations.Where(o => o.ProviderIdentityId == anchor.Id && o.Type == type).OrderByDescending(o => o.Version).FirstOrDefaultAsync(token);
                if (old is not null && old.RawPayloadId == raw.Id && old.CanonicalId == eventId && old.DateValue == day && old.TimestampValueUtc == kickoff && old.StatusValue == status) return old;
                var fact = new Observation(Guid.NewGuid(), anchor, new(CanonicalEntityKind.SportingEvent, eventId!.Value), type, raw.RetrievedAtUtc, now, now,
                    timestampValueUtc: kickoff, statusValue: status, rawPayloadId: raw.Id, corrects: old, dateValue: day);
                db.Add(fact); return fact;
            }
        }
        var state = fixtures.Any(f => f.Exclusions.Contains("identity_review_required")) ? ProviderPublicationState.ReviewRequired :
            fixtures.Any(f => f.DateObservationId is null) || fixtures.Count == 0 ? ProviderPublicationState.Blocked : ProviderPublicationState.Published;
        // Flush canonical rows first so receipt recording follows their DB clocks.
        // Both saves are inside the same transaction and roll back together.
        await db.SaveChangesAsync(token);
        var publishedAt = await QualityPersistence.Now(db, token);
        var report = new ProviderPublicationReport(1, Guid.NewGuid(), key, input, ApiFootballPublicationMapping.Version,
            claim.JobId, claim.ExecutionId, claim.Attempt, claim.Owner, raw.RetrievedAtUtc, raw.RecordedAtUtc, publishedAt, state, fixtures);
        var content = CanonicalDatasetJson.Serialize(report);
        db.Add(new ProviderPublicationRecord { Id = report.ReceiptId, SourceId = input.SourceId, RawId = raw.Id, PolicyId = input.PolicyId,
            ExecutionId = claim.ExecutionId, Key = key, Content = content, Hash = CanonicalDatasetJson.Hash(content) });
        await db.SaveChangesAsync(token);
        _ = await Raw(input, false, token); _ = await Map(input, raw, token); await jobs.CheckAsync(claim, token);
        await tx.CommitAsync(token);
        await tx.DisposeAsync();
        return await VerifyAsync(report.ReceiptId, false, token);
    }
    private static (CanonicalEntityKind Kind, string Reference)[] References(ApiFootballMappedFixture row) =>
        [(CanonicalEntityKind.Competition, row.CompetitionReference), (CanonicalEntityKind.Season, row.SeasonReference),
         (CanonicalEntityKind.Participant, row.HomeReference), (CanonicalEntityKind.Participant, row.AwayReference), (CanonicalEntityKind.SportingEvent, row.EventReference)];

    public async Task<ProviderPublicationArtifact> VerifyAsync(Guid receiptId, bool display, CancellationToken token = default)
    {
        // Independent context/connection: never verify pending tracked entities as committed evidence.
        await using var verify = new BetStatsDbContext(new DbContextOptionsBuilder<BetStatsDbContext>().UseNpgsql(db.Database.GetConnectionString()).Options);
        var receipt = await verify.Set<ProviderPublicationRecord>().AsNoTracking().SingleAsync(r => r.Id == receiptId, token);
        if (receipt.Hash != CanonicalDatasetJson.Hash(receipt.Content)) throw new InvalidDataException("Publication receipt hash mismatch.");
        var report = CanonicalDatasetJson.Deserialize<ProviderPublicationReport>(receipt.Content);
        if (report.Version != 1 || report.Provider != "api-football" || report.ReceiptId != receipt.Id || report.Key != receipt.Key || report.Input.RawId != receipt.RawId ||
            report.Input.SourceId != receipt.SourceId || report.Input.PolicyId != receipt.PolicyId || report.ExecutionId != receipt.ExecutionId || report.MappingVersion != ApiFootballPublicationMapping.Version)
            throw new InvalidDataException("Publication receipt manifest mismatch.");
        var raw = await Raw(report.Input, display, token); var mapped = await Map(report.Input, raw, token);
        if (report.RetrievedUtc != raw.RetrievedAtUtc || report.RawRecordedUtc != raw.RecordedAtUtc || report.PublishedUtc > receipt.RecordedUtc)
            throw new InvalidDataException("Receipt original clocks mismatch.");
        var parent = await verify.Set<PipelineExecution>().AsNoTracking().SingleAsync(e => e.Id == receipt.ExecutionId, token);
        var version = await verify.Set<PipelineJobVersion>().AsNoTracking().SingleAsync(v => v.JobId == parent.JobId && v.Version == parent.DefinitionVersion, token);
        if (parent.JobId != report.JobId || report.Attempt < 1 || report.Attempt > parent.Attempt || version.Hash != CanonicalDatasetJson.Hash(version.Content))
            throw new InvalidDataException("Receipt parent job mismatch.");
        var definition = CanonicalDatasetJson.Deserialize<PipelineDefinition>(version.Content);
        if (definition.Kind != PipelineKind.ProviderPublication || CanonicalDatasetJson.Fingerprint(JsonSerializer.Deserialize<ProviderPublicationInput>(definition.PayloadJson, PipelineOperatorCommand.JsonOptions)) != CanonicalDatasetJson.Fingerprint(report.Input))
            throw new InvalidDataException("Receipt original operation mismatch.");
        var decisionIds = report.Fixtures.SelectMany(f => f.Bindings).Select(b => b.DecisionId).Where(id => id is not null).Select(id => id!.Value).Distinct().ToArray();
        var decisions = await verify.IdentityResolutions.AsNoTracking().Where(d => decisionIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, token);
        var observationIds = report.Fixtures.SelectMany(f => new[] { f.DateObservationId, f.ScheduleObservationId, f.StatusObservationId }).Where(id => id is not null).Select(id => id!.Value).ToArray();
        var observations = await verify.Observations.AsNoTracking().Where(o => observationIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, token);
        var timeIds = report.Fixtures.Where(f => f.TimeEvidenceId is not null).Select(f => f.TimeEvidenceId!.Value).ToArray();
        var times = await verify.EventTimeEvidence.AsNoTracking().Where(t => timeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, token);
        var resultIds = report.Fixtures.Where(f => f.ResultObservationId is not null).Select(f => f.ResultObservationId!.Value).ToArray();
        var results = await verify.FootballResults.AsNoTracking().Where(r => resultIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, token);
        foreach (var fixture in report.Fixtures)
        {
            foreach (var binding in fixture.Bindings)
                if (binding.DecisionId is not { } decisionId || !decisions.TryGetValue(decisionId, out var d) || d.ProviderIdentityId != binding.IdentityId ||
                    d.Version != binding.Version || d.DataSourceId != receipt.SourceId || d.CanonicalId != binding.CanonicalId)
                    throw new InvalidDataException("Receipt identity decision missing.");
            foreach (var id in new[] { fixture.DateObservationId, fixture.ScheduleObservationId, fixture.StatusObservationId }.Where(id => id is not null))
                if (!observations.TryGetValue(id!.Value, out var o) || o.RawPayloadId != receipt.RawId || o.CanonicalSportingEventId != fixture.EventId ||
                    o.RetrievedAtUtc != report.RetrievedUtc || o.AvailableAtUtc > report.PublishedUtc || o.RecordedAtUtc > receipt.RecordedUtc)
                    throw new InvalidDataException("Receipt observation missing.");
            if (fixture.TimeEvidenceId is { } time && (!times.TryGetValue(time, out var t) || t.RawId != receipt.RawId || t.DateObservationId != fixture.DateObservationId ||
                t.Value.SourceUtcInstant != fixture.KickoffUtc || mapped.Single(m => m.EventReference == fixture.Reference).KickoffUtc != fixture.KickoffUtc)) throw new InvalidDataException("Receipt kickoff missing.");
            if (fixture.ResultObservationId is { } result && (!results.TryGetValue(result, out var r) || r.RawId != receipt.RawId || r.EventId != fixture.EventId ||
                r.Value != mapped.Single(m => m.EventReference == fixture.Reference).FinalResult)) throw new InvalidDataException("Receipt result missing.");
        }
        return new(report, receipt.Hash, receipt.RecordedUtc, true);
    }
    public async Task<ProviderPublicationStatus> StatusAsync(Guid jobId, bool display, CancellationToken token = default)
    {
        var job = await db.Set<PipelineJob>().AsNoTracking().SingleAsync(j => j.Id == jobId, token);
        var version = await db.Set<PipelineJobVersion>().AsNoTracking().SingleAsync(v => v.JobId == jobId && v.Version == job.Version, token);
        if (version.Hash != CanonicalDatasetJson.Hash(version.Content)) throw new InvalidDataException("Publication definition hash mismatch.");
        var definition = CanonicalDatasetJson.Deserialize<PipelineDefinition>(version.Content);
        if (definition.Kind != PipelineKind.ProviderPublication) throw new ArgumentException("Provider publication job required.");
        var input = JsonSerializer.Deserialize<ProviderPublicationInput>(definition.PayloadJson, PipelineOperatorCommand.JsonOptions) ?? throw new InvalidDataException("Publication input missing.");
        var original = await Raw(input, display, token);
        var rawState = "Unavailable"; var parsingState = "Unavailable";
        try
        {
            var bytes = await store.ReadAsync(new(original.StorageKey, original.ContentHashSha256, original.ByteLength!.Value), token);
            rawState = "Verified";
            try { _ = ApiFootballPublicationMapping.Map(bytes, Context(input, original), token); parsingState = "Parsed"; }
            catch (InvalidDataException) { parsingState = "Rejected"; }
        }
        catch (IOException) { rawState = "IntegrityFailure"; }
        // A replay in another job references the original immutable receipt, rather
        // than manufacturing a receipt with a different publishing parent.
        var ids = await db.Set<ProviderPublicationRecord>().AsNoTracking().Where(r => r.RawId == input.RawId && r.SourceId == input.SourceId)
            .OrderByDescending(r => r.RecordedUtc).Select(r => r.Id).Take(100).ToArrayAsync(token);
        var receipts = new List<ProviderPublicationArtifact>(); foreach (var id in ids) receipts.Add(await VerifyAsync(id, display, token));
        var attempts = await db.Set<PipelineExecution>().AsNoTracking().Where(e => e.JobId == jobId).OrderByDescending(e => e.StartedUtc).Take(100)
            .Select(e => new AcquisitionExecutionStatus(e.Id, e.State, e.Attempt, e.PlannedUtc, e.StartedUtc, e.CompletedUtc)).ToArrayAsync(token);
        var operations = await db.Set<PipelineReceipt>().AsNoTracking().Where(r => r.JobId == jobId).OrderByDescending(r => r.RecordedUtc).Take(100)
            .Select(r => new PublicationOperationState(r.ExecutionId, r.State, r.Category, r.RecordedUtc)).ToArrayAsync(token);
        return new(jobId, job.State, receipts, attempts, input.RawId, input.RawHash, input.Profile, input.Scope, rawState, parsingState, operations);
    }

    public async Task<AcquisitionArtifact> AcquisitionAsync(Guid executionId, bool display, CancellationToken token)
    {
        var artifact = await db.Set<PipelineArtifact>().AsNoTracking().SingleAsync(a => a.ExecutionId == executionId, token);
        if (artifact.Hash != CanonicalDatasetJson.Hash(artifact.Content) || !await db.Set<PipelineExecution>().AnyAsync(e => e.Id == executionId && e.State == PipelineState.Completed, token))
            throw new InvalidDataException("Completed publication acknowledgment required.");
        using var doc = JsonDocument.Parse(artifact.Content); var result = doc.RootElement.GetProperty("Result");
        if (result.GetProperty("Kind").GetString() != "provider-publication-v1") throw new InvalidDataException("Provider publication output required.");
        var receipt = await VerifyAsync(result.GetProperty("ReceiptId").GetGuid(), display, token); var r = receipt.Report; var i = r.Input;
        if (r.State != ProviderPublicationState.Published || result.GetProperty("Hash").GetString() != receipt.Hash)
            throw new InvalidDataException("Acknowledgment receipt binding mismatch.");
        var execution = await db.Set<PipelineExecution>().AsNoTracking().SingleAsync(e => e.Id == executionId, token);
        var definition = await db.Set<PipelineJobVersion>().AsNoTracking().SingleAsync(v => v.JobId == execution.JobId && v.Version == execution.DefinitionVersion, token);
        if (definition.Hash != CanonicalDatasetJson.Hash(definition.Content)) throw new InvalidDataException("Publication definition corrupt.");
        var input = JsonSerializer.Deserialize<ProviderPublicationInput>(CanonicalDatasetJson.Deserialize<PipelineDefinition>(definition.Content).PayloadJson, PipelineOperatorCommand.JsonOptions);
        if (CanonicalDatasetJson.Fingerprint(input) != CanonicalDatasetJson.Fingerprint(i)) throw new InvalidDataException("Publication acknowledgment input mismatch.");
        var raw = await Raw(i, display, token); var observations = new List<AcquisitionObservation>();
        foreach (var f in r.Fixtures.Where(f => f.DateObservationId is not null))
        {
            var date = await db.Observations.AsNoTracking().SingleAsync(o => o.Id == f.DateObservationId, token);
            var time = await db.EventTimeEvidence.AsNoTracking().SingleAsync(t => t.Id == f.TimeEvidenceId, token);
            var final = f.ResultObservationId is { } id ? await db.FootballResults.AsNoTracking().SingleAsync(x => x.Id == id, token) : null;
            var exclusions = f.Exclusions.Where(e => e is not ("unsupported_final_result_status_or_basis" or "missing_final_result" or "missing_half_time_result")).ToList();
            var quality = await gate.EvaluateAsync(new(date.Id, receipt.RecordedUtc, DataPurpose.InternalAnalytics, new(IntendedRetentionDays: 1)), token);
            exclusions.AddRange(quality.Reasons);
            var finalEligible = false;
            if (final is not null)
            {
                var evidence = await footballResults.ReadAsync(new(f.Bindings[0].CanonicalId!.Value, f.Bindings[1].CanonicalId!.Value,
                    receipt.RecordedUtc, DataPurpose.InternalAnalytics, new(IntendedRetentionDays: 1), f.EventId, i.SourceId), token);
                finalEligible = evidence.Results.Count == 1 && evidence.Results[0].Eligible && evidence.Results[0].Labels is not null;
            }
            // Origin and chronology do not establish independent inventory coverage.
            observations.Add(new(f.EventId!.Value, date.Id, f.Bindings[4].DecisionId!.Value, f.Bindings[2].CanonicalId!.Value, f.Bindings[3].CanonicalId!.Value,
                f.Bindings[0].CanonicalId!.Value, f.Bindings[1].CanonicalId!.Value, date.DateValue!.Value, date.AvailableAtUtc, date.RecordedAtUtc, null,
                time.Value.SourceUtcInstant, EventTimePrecision.Second, time.Id, time.RecordedAtUtc, time.AvailableAtUtc, final?.Id,
                finalEligible, date.RecordedAtUtc >= time.Value.SourceUtcInstant,
                exclusions, null, final?.PublishedAtUtc, final?.AvailableAtUtc, final?.RecordedAtUtc, time.Value));
        }
        var rawEvidence = new AcquisitionRaw(raw.Id, raw.ContentHashSha256, raw.ByteLength!.Value, raw.RetrievedAtUtc, raw.RecordedAtUtc);
        var capability = new AcquisitionCapability(1, i.SourceId, "api-football", i.PolicyId, i.PolicyVersion, FootballTransport.Api, i.Profile,
            [AcquisitionCategory.PrematchMetadata, AcquisitionCategory.PostmatchResults, AcquisitionCategory.HistoricalResults]);
        return new(executionId, artifact.Hash, new(1, CanonicalDatasetJson.Fingerprint(i), capability, i.Scope,
            observations.Any(o => o.FinalResultEligible) ? AcquisitionCategory.PostmatchResults : AcquisitionCategory.PrematchMetadata,
            i.EvidenceKind, AcquisitionState.Verified, receipt.RecordedUtc, r.ReceiptId, "Published", false, rawEvidence, observations,
            ["independent_coverage_evidence_required", "prediction_and_evaluation_gates_unchanged"], [rawEvidence]));
    }
}
