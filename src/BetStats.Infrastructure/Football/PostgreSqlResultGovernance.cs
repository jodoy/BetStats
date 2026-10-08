using System.Data;
using System.Text.Json;
using BetStats.Application.Coverage;
using BetStats.Application.Datasets;
using BetStats.Application.Football;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Application.Quality;
using BetStats.Domain.Football;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Football;

public sealed class PostgreSqlResultGovernance(BetStatsDbContext db, IFootballResults results, IRawPayloadStore store,
    ISourcePolicyEvaluator policies, ISourceOperationalStatus sources, IAnalyticalQualityGate gate) : IResultGovernance
{
    private static void Utc(DateTime value) { if (value.Kind != DateTimeKind.Utc || value.Ticks % 10 != 0) throw new ArgumentException("UTC microseconds required."); }
    private static void Mutation(string actor, string reason, bool approved) { QualityPersistence.Operator(actor, reason); if (!approved) throw new ArgumentException("Explicit mutation approval required."); }
    private async Task<Guid> Authorize(Guid source, DateTime retrieved, DataPurpose purpose, UsageContext context, DateTime knowledge, CancellationToken token)
    {
        var now = await QualityPersistence.Now(db, token);
        if (await sources.ReadAsync(source, token) != SourceOperationalStatus.Enabled) throw new UnauthorizedAccessException("Result evidence source disabled.");
        Guid? policy = null;
        foreach (var time in new[] { knowledge, now }.Distinct()) foreach (var p in new[] { purpose, DataPurpose.InternalAnalytics, DataPurpose.RawPayloadStorage, DataPurpose.HistoricalRetention }.Distinct())
        {
            var decision = await policies.EvaluateAsync(source, p, time, context, token);
            if (!decision.Allowed || decision.Restrictions.Any(r => r.MaximumRetentionDays is { } days && now - retrieved > TimeSpan.FromDays(days)))
                throw new UnauthorizedAccessException("Result evidence usage denied.");
            policy = decision.PolicyId;
        }
        return policy ?? throw new UnauthorizedAccessException("Policy evidence missing.");
    }
    private async Task<byte[]> Bytes(RawPayload raw, DateTime cutoff, DataPurpose purpose, UsageContext context, DateTime knowledge, CancellationToken token)
    {
        await Authorize(raw.DataSourceId, raw.RetrievedAtUtc, purpose, context, knowledge, token);
        if (raw.RecordedAtUtc > cutoff || raw.RetrievedAtUtc > cutoff || raw.ByteLength is not { } length) throw new InvalidDataException("RAW not known at cutoff.");
        return (await store.ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, length), token)).ToArray();
    }
    private static bool InScope(FootballResultObservation r, ResultCoverageScope scope) => r.SourceId == scope.SourceId && r.CompetitionId == scope.CompetitionId && r.SeasonId == scope.SeasonId &&
        r.CompetitionReference == scope.CompetitionReference && r.SeasonReference == scope.SeasonReference && r.EventDate >= scope.Interval.StartDate && r.EventDate < scope.Interval.EndDate &&
        (scope.ParticipantId is null || r.HomeId == scope.ParticipantId || r.AwayId == scope.ParticipantId);
    private static void ValidateClaim(ResultInventoryClaim claim)
    {
        claim.Scope.Validate();
        if (claim.PublishedUtc is { } published) Utc(published);
        if (claim.Version != 1 || claim.Contract != ResultCoverageRules.OwnedContract || claim.Claim is not (ResultCoverageStatus.Unknown or ResultCoverageStatus.Partial or ResultCoverageStatus.Complete or ResultCoverageStatus.Empty) ||
            claim.ExpectedEventReferences is null || claim.ResultObservationIds is null || claim.ExpectedEventReferences.Count > 1000 || claim.ResultObservationIds.Count > 1000 ||
            claim.ExpectedEventReferences.Any(r => string.IsNullOrWhiteSpace(r) || r.Length > 500) || claim.ResultObservationIds.Any(r => r == Guid.Empty) ||
            claim.ExpectedEventReferences.Distinct().Count() != claim.ExpectedEventReferences.Count || claim.ResultObservationIds.Distinct().Count() != claim.ResultObservationIds.Count ||
            claim.Claim == ResultCoverageStatus.Empty && (claim.ExpectedEventReferences.Count != 0 || claim.ResultObservationIds.Count != 0) ||
            claim.Claim == ResultCoverageStatus.Complete && (claim.ExpectedEventReferences.Count == 0 || claim.ResultObservationIds.Count == 0))
            throw new ArgumentException("Explicit bounded result inventory contract required.");
    }
    public async Task<ResultInventoryEvidence> RecordAsync(ResultInventorySubmission request, CancellationToken token = default)
    {
        Mutation(request.OperatorId, request.Reason, request.Approved); ValidateClaim(request.Claim); Utc(request.AvailableUtc); Utc(request.ValidUntilUtc); if (request.PublishedUtc is { } p) Utc(p);
        var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == request.RawId && r.DataSourceId == request.Claim.Scope.SourceId, token);
        var now = await QualityPersistence.Now(db, token);
        var proof = CanonicalDatasetJson.Deserialize<ResultInventoryClaim>(await Bytes(raw, now, DataPurpose.InternalAnalytics, new(), now, token));
        if (CanonicalDatasetJson.Fingerprint(proof) != CanonicalDatasetJson.Fingerprint(request.Claim) || request.PublishedUtc != proof.PublishedUtc) throw new InvalidDataException("Result inventory RAW mismatch.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token); await QualityPersistence.Lock(db, raw.DataSourceId, token); now = await QualityPersistence.Now(db, token);
        var policy = await Authorize(raw.DataSourceId, raw.RetrievedAtUtc, DataPurpose.InternalAnalytics, new(), now, token);
        if (request.AvailableUtc < raw.RetrievedAtUtc || request.AvailableUtc > now || request.ValidUntilUtc <= now || request.PublishedUtc > raw.RetrievedAtUtc) throw new ArgumentException("Invalid inventory clocks.");
        var prior = request.CorrectsId is { } id ? await db.ResultInventory.AsNoTracking().SingleAsync(e => e.Id == id, token) : null;
        if (prior is not null && (prior.Scope != request.Claim.Scope || request.AvailableUtc <= prior.AvailableAtUtc || raw.RetrievedAtUtc <= prior.RetrievedAtUtc ||
            await db.ResultInventory.AnyAsync(e => e.CorrectsId == prior.Id, token))) throw new InvalidOperationException("Explicit newer same-scope inventory correction required.");
        var evidence = new ResultInventoryEvidence { Id = Guid.NewGuid(), SourceId = raw.DataSourceId, Scope = proof.Scope, Claim = proof.Claim, RawId = raw.Id, RawHash = raw.ContentHashSha256,
            PolicyId = policy, CorrectsId = prior?.Id, Version = (prior?.Version ?? 0) + 1, PublishedAtUtc = request.PublishedUtc, RetrievedAtUtc = raw.RetrievedAtUtc,
            AvailableAtUtc = request.AvailableUtc, ValidUntilUtc = request.ValidUntilUtc, OperatorId = request.OperatorId, Reason = request.Reason };
        db.Add(evidence); await db.SaveChangesAsync(token); await tx.CommitAsync(token); return evidence;
    }
    private async Task<bool> InventoryValid(ResultInventoryEvidence evidence, ResultInventoryClaim proof, FootballResultQuery q, CancellationToken token)
    {
        ValidateClaim(proof);
        if (proof.Scope != evidence.Scope || proof.Claim != evidence.Claim) return false;
        var policy = await db.SourcePolicies.AsNoTracking().SingleAsync(p => p.Id == evidence.PolicyId, token);
        if (policy.TermsReference != "synthetic:owned-fixture") return false;
        var report = await results.ReadAsync(q with { SourceId = evidence.SourceId, EventId = null, IncludeSuperseded = false }, token);
        var potential = report.Results.Where(r => InScope(r.Observation, evidence.Scope) && r.Observation.Value.Status == FootballMatchStatus.Finished).ToArray();
        if (proof.Claim == ResultCoverageStatus.Unknown) return true;
        if (proof.Claim == ResultCoverageStatus.Partial)
        {
            var selected = potential.Where(r => proof.ResultObservationIds.Contains(r.Observation.Id)).ToArray();
            return selected.All(r => r.Eligible && FootballResultRules.LabelEligible(r.Observation.Value)) && selected.Length == proof.ResultObservationIds.Count &&
                selected.Select(r => r.Observation.SourceEventReference).Distinct().Order(StringComparer.Ordinal).SequenceEqual(proof.ExpectedEventReferences.Order(StringComparer.Ordinal));
        }
        if (evidence.Scope.Interval.EndDate > DateOnly.FromDateTime(q.AsOfUtc)) return false;
        if (potential.Any(r => !r.Eligible || !FootballResultRules.LabelEligible(r.Observation.Value))) return false;
        var ids = potential.Select(r => r.Observation.Id).Order().ToArray();
        var references = potential.Select(r => r.Observation.SourceEventReference).Distinct().Order(StringComparer.Ordinal).ToArray();
        if (!ids.SequenceEqual(proof.ResultObservationIds.Order()) || !references.SequenceEqual(proof.ExpectedEventReferences.Order(StringComparer.Ordinal))) return false;
        // A metadata-only finished row is a missing result, never affirmative empty/complete evidence.
        var dates = await db.Observations.AsNoTracking().Where(o => o.DataSourceId == evidence.SourceId && o.Type == ObservationType.EventDate &&
            o.AvailableAtUtc <= q.AsOfUtc && o.RecordedAtUtc <= q.AsOfUtc && o.DateValue >= evidence.Scope.Interval.StartDate && o.DateValue < evidence.Scope.Interval.EndDate &&
            !db.Observations.Any(n => n.ProviderIdentityId == o.ProviderIdentityId && n.Type == o.Type && n.Version > o.Version && n.AvailableAtUtc <= q.AsOfUtc && n.RecordedAtUtc <= q.AsOfUtc))
            .OrderBy(o => o.Id).Take(1001).ToArrayAsync(token);
        if (dates.Length > 1000) throw new InvalidOperationException("Result inventory metadata bound exceeded.");
        foreach (var date in dates)
        {
            var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == date.RawPayloadId, token);
            var binding = await FootballContext.ReadAsync(db, raw, new(evidence.Scope.CompetitionReference, evidence.Scope.SeasonReference), token, q.AsOfUtc);
            if (binding != new FootballImportScope(evidence.Scope.CompetitionReference, evidence.Scope.SeasonReference)) continue;
            var identity = await db.ProviderIdentities.AsNoTracking().SingleAsync(i => i.Id == date.ProviderIdentityId, token);
            var parsed = new FootballFixtureParser().Parse(await Bytes(raw, q.AsOfUtc, q.Purpose, q.Context, q.ReconstructionAtUtc ?? q.AsOfUtc, token), binding, token);
            var row = parsed.Records.SingleOrDefault(r => r.MatchReference == identity.ExternalId);
            if (row is null) return false;
            if (evidence.Scope.ParticipantId is { } participant)
            {
                var home = await QualityPersistence.Anchor(db, evidence.SourceId, CanonicalEntityKind.Participant, row.HomeReference, token);
                var away = await QualityPersistence.Anchor(db, evidence.SourceId, CanonicalEntityKind.Participant, row.AwayReference, token);
                var h = await QualityPersistence.Decision(db, home?.Id, q.ReconstructionAtUtc ?? q.AsOfUtc, token);
                var a = await QualityPersistence.Decision(db, away?.Id, q.ReconstructionAtUtc ?? q.AsOfUtc, token);
                if (h?.Status != ResolutionStatus.Resolved || a?.Status != ResolutionStatus.Resolved) return false;
                if (h.CanonicalParticipantId != participant && a.CanonicalParticipantId != participant) continue;
            }
            var status = await db.Observations.AsNoTracking().Where(o => o.ProviderIdentityId == date.ProviderIdentityId && o.Type == ObservationType.EventStatus &&
                o.AvailableAtUtc <= q.AsOfUtc && o.RecordedAtUtc <= q.AsOfUtc).OrderByDescending(o => o.Version).FirstOrDefaultAsync(token);
            if (status is null) return false;
            foreach (var observation in new[] { date, status })
                if (!(await gate.EvaluateAsync(new(observation.Id, q.AsOfUtc, q.Purpose, q.Context, q.Mode, q.ReconstructionAtUtc), token)).Eligible) return false;
            if (status.StatusValue == SportingEventStatus.Completed && !references.Contains(row.MatchReference, StringComparer.Ordinal)) return false;
        }
        return true;
    }
    public async Task<ResultInventoryReview> ReviewAsync(ResultReviewRequest request, CancellationToken token = default)
    {
        Mutation(request.OperatorId, request.Reason, request.Approved); if (request.ExpectedSequence < 0) throw new ArgumentException("Nonnegative review sequence required.");
        var evidence = await db.ResultInventory.AsNoTracking().SingleAsync(e => e.Id == request.EvidenceId, token);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token); await QualityPersistence.Lock(db, evidence.SourceId, token);
        var now = await QualityPersistence.Now(db, token); var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == evidence.RawId, token);
        var proof = CanonicalDatasetJson.Deserialize<ResultInventoryClaim>(await Bytes(raw, now, DataPurpose.InternalAnalytics, new(), now, token));
        var valid = evidence.ValidUntilUtc > now && !await db.ResultInventory.AnyAsync(e => e.CorrectsId == evidence.Id, token) &&
            await InventoryValid(evidence, proof, new(evidence.Scope.CompetitionId, evidence.Scope.SeasonId, now, DataPurpose.InternalAnalytics, new(), SourceId: evidence.SourceId), token);
        var sequence = await db.ResultInventoryReviews.Where(r => r.EvidenceId == evidence.Id).Select(r => (int?)r.Sequence).MaxAsync(token) ?? 0;
        if (sequence != request.ExpectedSequence) throw new InvalidOperationException("Concurrent result inventory review conflict.");
        var review = new ResultInventoryReview { Id = Guid.NewGuid(), EvidenceId = evidence.Id, Sequence = sequence + 1, Approved = request.Approve && valid, OperatorId = request.OperatorId, Reason = request.Reason };
        db.Add(review); await db.SaveChangesAsync(token); await tx.CommitAsync(token); return review;
    }
    public async Task<ResultCoverageReport> ReportAsync(ResultCoverageQuery query, CancellationToken token = default)
    {
        query.Scope.Validate(); var q = query.Results; Utc(q.AsOfUtc); var knowledge = q.ReconstructionAtUtc ?? q.AsOfUtc;
        if (q.SourceId != query.Scope.SourceId || q.CompetitionId != query.Scope.CompetitionId || q.SeasonId != query.Scope.SeasonId || q.EventId is not null || q.IncludeSuperseded)
            throw new ArgumentException("Result coverage query must match its explicit inventory scope.");
        // Validates mode, purposes, knowledge clocks and result RAW independently of inventory rows.
        var report = await results.ReadAsync(q, token); var now = await QualityPersistence.Now(db, token);
        await Authorize(query.Scope.SourceId, now, q.Purpose, q.Context, knowledge, token);
        var candidates = await db.ResultInventory.AsNoTracking().Where(e => e.SourceId == query.Scope.SourceId && e.AvailableAtUtc <= q.AsOfUtc && e.RecordedAtUtc <= q.AsOfUtc &&
            !db.ResultInventory.Any(n => n.CorrectsId == e.Id && n.AvailableAtUtc <= q.AsOfUtc && n.RecordedAtUtc <= q.AsOfUtc)).OrderBy(e => e.Id).Take(201).ToArrayAsync(token);
        if (candidates.Length > 200) throw new InvalidOperationException("Result inventory bound exceeded.");
        var items = new List<ResultCoverageItem>();
        foreach (var e in candidates.Where(e => e.Scope.SameDimensions(query.Scope) && CoverageRules.Intersection(e.Scope.Interval, query.Scope.Interval) is not null))
        {
            var review = await db.ResultInventoryReviews.AsNoTracking().Where(r => r.EvidenceId == e.Id && r.RecordedAtUtc <= knowledge).OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(token);
            ResultInventoryClaim? proof = null; var status = ResultCoverageStatus.Unknown; var reasons = new List<string>();
            try
            {
                var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == e.RawId, token);
                proof = CanonicalDatasetJson.Deserialize<ResultInventoryClaim>(await Bytes(raw, q.AsOfUtc, q.Purpose, q.Context, knowledge, token));
                if (raw.ContentHashSha256 != e.RawHash || proof.Scope != e.Scope || proof.Claim != e.Claim || proof.PublishedUtc != e.PublishedAtUtc) throw new InvalidDataException("Inventory binding mismatch.");
                ValidateClaim(proof);
                if (e.ValidUntilUtc <= now || e.ValidUntilUtc <= knowledge) { status = ResultCoverageStatus.Expired; reasons.Add("result_inventory_expired"); }
                else if (review?.Approved == true)
                {
                    if (await InventoryValid(e, proof, q, token)) status = e.Claim;
                    else { status = ResultCoverageStatus.Conflict; reasons.Add("result_inventory_no_longer_matches"); }
                }
                else reasons.Add("result_inventory_not_approved_at_knowledge_cutoff");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException)
            { reasons.Add("result_inventory_raw_or_contract_unavailable"); proof = null; }
            items.Add(new(e, review, status, proof, reasons));
        }
        var observed = report.Results.Where(r => InScope(r.Observation, query.Scope)).ToArray();
        var conflict = observed.Any(r => r.Reasons.Any(s => s.Contains("conflict", StringComparison.Ordinal)));
        var classification = ResultCoverageRules.Classify(query.Scope, items, conflict);
        return new(1, query, classification, items, observed.Where(r => r.Eligible && FootballResultRules.LabelEligible(r.Observation.Value)).Select(r => r.Observation.Id).Order().ToArray(),
            items.Count == 0 ? ["independent_result_inventory_missing"] : items.SelectMany(i => i.Reasons).Distinct().Order(StringComparer.Ordinal).ToArray());
    }
    public async Task<EventEndEvidence> RecordEndAsync(EventEndSubmission request, CancellationToken token = default)
    {
        Mutation(request.OperatorId, request.Reason, request.Approved); Utc(request.AvailableUtc); if (request.PublishedUtc is { } p) Utc(p);
        var claim = request.Claim; var resolution = EventTimeRules.Resolve(claim.Value);
        var result = await db.FootballResults.AsNoTracking().SingleAsync(r => r.Id == claim.ResultObservationId, token);
        var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == request.RawId && r.DataSourceId == result.SourceId, token);
        var now = await QualityPersistence.Now(db, token);
        var proof = CanonicalDatasetJson.Deserialize<EventEndSourceClaim>(await Bytes(raw, now, DataPurpose.InternalAnalytics, new(), now, token));
        if (CanonicalDatasetJson.Fingerprint(claim) != CanonicalDatasetJson.Fingerprint(proof) || request.PublishedUtc != proof.PublishedUtc || claim.Version != 1 || claim.OriginalRawId != result.RawId ||
            claim.ProviderEventReference != result.SourceEventReference || claim.Context != new FootballTimeContext(result.CompetitionReference, result.SeasonReference) ||
            result.Value.Status != FootballMatchStatus.Finished || resolution.UtcInstant > raw.RetrievedAtUtc || claim.Value.LocalDate < result.EventDate)
            throw new InvalidDataException("Event end must be an explicit original-source finished-result claim.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token); await QualityPersistence.Lock(db, result.SourceId, token); now = await QualityPersistence.Now(db, token);
        var policy = await Authorize(raw.DataSourceId, raw.RetrievedAtUtc, DataPurpose.InternalAnalytics, new(), now, token);
        var current = await results.ReadAsync(new(result.CompetitionId, result.SeasonId, now, DataPurpose.InternalAnalytics, new(), result.EventId, result.SourceId), token);
        var valid = current.Results.SingleOrDefault(r => r.Observation.Id == result.Id);
        if (valid?.Eligible != true || !FootballResultRules.LabelEligible(result.Value)) throw new InvalidDataException("Current eligible result identity/quality required.");
        if (request.AvailableUtc < raw.RetrievedAtUtc || request.AvailableUtc > now || request.PublishedUtc > raw.RetrievedAtUtc) throw new ArgumentException("Invalid end provenance clocks.");
        var previous = request.CorrectsId is { } id ? await db.EventEnds.AsNoTracking().SingleAsync(e => e.Id == id, token) : null;
        if (previous is not null)
        {
            var prior = await db.FootballResults.AsNoTracking().SingleAsync(r => r.Id == previous.ResultObservationId, token);
            if (prior.ProviderIdentityId != result.ProviderIdentityId || prior.SourceId != result.SourceId || request.AvailableUtc <= previous.AvailableAtUtc || raw.RetrievedAtUtc <= previous.RetrievedAtUtc ||
                request.PublishedUtc is not null && previous.PublishedAtUtc is not null && request.PublishedUtc <= previous.PublishedAtUtc || await db.EventEnds.AnyAsync(e => e.CorrectsId == previous.Id, token))
                throw new InvalidOperationException("Explicit newer same-stream event-end correction required.");
        }
        var decision = await QualityPersistence.Decision(db, result.ProviderIdentityId, now, token);
        if (decision?.Status != ResolutionStatus.Resolved || decision.CanonicalSportingEventId != result.EventId) throw new InvalidDataException("Reviewed end identity required.");
        var evidence = new EventEndEvidence { Id = Guid.NewGuid(), SourceId = result.SourceId, ResultObservationId = result.Id, RawId = raw.Id, RawHash = raw.ContentHashSha256, PolicyId = policy,
            IdentityDecisionId = decision.Id, Value = claim.Value, CorrectsId = previous?.Id, Version = (previous?.Version ?? 0) + 1, PublishedAtUtc = request.PublishedUtc,
            RetrievedAtUtc = raw.RetrievedAtUtc, AvailableAtUtc = request.AvailableUtc, OperatorId = request.OperatorId, Reason = request.Reason };
        db.Add(evidence); await db.SaveChangesAsync(token); await tx.CommitAsync(token); return evidence;
    }
    public async Task<IReadOnlyList<EventEndClaimResult>> EndsAsync(FootballResultQuery q, CancellationToken token = default)
    {
        var report = await results.ReadAsync(q with { IncludeSuperseded = false }, token); var ids = report.Results.Select(r => r.Observation.Id).ToArray();
        var claims = await db.EventEnds.AsNoTracking().Where(e => ids.Contains(e.ResultObservationId) && e.AvailableAtUtc <= q.AsOfUtc && e.RecordedAtUtc <= q.AsOfUtc &&
            !db.EventEnds.Any(n => n.CorrectsId == e.Id && n.AvailableAtUtc <= q.AsOfUtc && n.RecordedAtUtc <= q.AsOfUtc)).OrderBy(e => e.Id).Take(201).ToArrayAsync(token);
        if (claims.Length > 200) throw new InvalidOperationException("Event-end bound exceeded.");
        var output = new List<EventEndClaimResult>();
        foreach (var end in claims)
        {
            var r = report.Results.Single(r => r.Observation.Id == end.ResultObservationId); var reasons = r.Reasons.ToList(); var resolution = EventTimeRules.Resolve(end.Value);
            var decision = await QualityPersistence.Decision(db, r.Observation.ProviderIdentityId, q.ReconstructionAtUtc ?? q.AsOfUtc, token);
            if (decision?.Status != ResolutionStatus.Resolved || decision.CanonicalSportingEventId != r.Observation.EventId) reasons.Add("event_end_identity_unresolved");
            try
            {
                var raw = await db.RawPayloads.AsNoTracking().SingleAsync(x => x.Id == end.RawId, token);
                var proof = CanonicalDatasetJson.Deserialize<EventEndSourceClaim>(await Bytes(raw, q.AsOfUtc, q.Purpose, q.Context, q.ReconstructionAtUtc ?? q.AsOfUtc, token));
                if (raw.DataSourceId != end.SourceId || raw.ContentHashSha256 != end.RawHash || proof.Version != 1 || proof.OriginalRawId != r.Observation.RawId ||
                    proof.ResultObservationId != r.Observation.Id || proof.ProviderEventReference != r.Observation.SourceEventReference || proof.Context != new FootballTimeContext(r.Observation.CompetitionReference, r.Observation.SeasonReference) ||
                    proof.Value != end.Value || proof.PublishedUtc != end.PublishedAtUtc || resolution.UtcInstant > raw.RetrievedAtUtc) reasons.Add("event_end_original_context_mismatch");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { reasons.Add("event_end_raw_integrity_or_context"); }
            if (resolution.UtcInstant is null || resolution.Precision is not (BetStats.Domain.Coverage.EventTimePrecision.Minute or BetStats.Domain.Coverage.EventTimePrecision.Second)) reasons.Add("precise_event_end_required");
            if (!FootballResultRules.LabelEligible(r.Observation.Value)) reasons.Add("finished_regulation_result_required");
            if (end.Value.LocalDate < r.Observation.EventDate) reasons.Add("event_end_precedes_event_date");
            if (claims.Any(other => other.Id != end.Id && report.Results.Single(x => x.Observation.Id == other.ResultObservationId).Observation.EventId == r.Observation.EventId && EventTimeRules.Conflicts(end.Value, other.Value)))
                reasons.Add("event_end_conflict");
            output.Add(new(end, r.Observation.EventId, decision?.Id ?? Guid.Empty, resolution, reasons.Count == 0, reasons.Distinct().Order(StringComparer.Ordinal).ToArray()));
        }
        return output;
    }
    public async Task<ResultDatasetGovernance> FreezeAsync(FootballResultManifest manifest, CancellationToken token = default)
    {
        var d = manifest.MetadataManifest.Definition; var rows = new List<ResultGovernanceRow>();
        foreach (var row in manifest.Rows)
        {
            var day = DateOnly.FromDateTime(row.Metadata.PredictionCutoffUtc);
            var scope = new ResultCoverageScope(row.Metadata.Target.SourceId, d.CompetitionId, d.SeasonId, null, d.CompetitionReference, d.SeasonReference,
                new(BetStats.Domain.Coverage.IntervalKind.Calendar, null, null, day.AddDays(-30), day, "UTC-calendar"));
            var feature = await ReportAsync(new(scope, row.FeatureEvidence.Query), token);
            var labelScope = scope with { Interval = scope.Interval with { StartDate = row.Metadata.Target.EventDate, EndDate = row.Metadata.Target.EventDate.AddDays(1) } };
            var label = await ReportAsync(new(labelScope, row.LabelEvidence.Query with { EventId = null }), token);
            rows.Add(new(row.Metadata.EventId, feature, label, await EndsAsync(row.LabelEvidence.Query, token)));
        }
        return new(1, rows);
    }
    public async Task EnsureCurrentAsync(ResultDatasetGovernance governance, FootballResultManifest manifest, CancellationToken token = default)
    {
        var d = manifest.MetadataManifest.Definition;
        foreach (var item in governance.Rows.SelectMany(r => r.FeatureCoverage.Items.Concat(r.LabelCoverage.Items)).DistinctBy(i => i.Evidence.Id).OrderBy(i => i.Evidence.SourceId))
        {
            var e = item.Evidence; var now = await QualityPersistence.Now(db, token);
            await Authorize(e.SourceId, e.RetrievedAtUtc, d.Purpose, d.Context, now, token);
            if (item.Status is ResultCoverageStatus.Complete or ResultCoverageStatus.Empty && e.ValidUntilUtc <= now) throw new UnauthorizedAccessException("Frozen result inventory expired.");
        }
        foreach (var end in governance.Rows.SelectMany(r => r.Ends).DistinctBy(e => e.Evidence.Id).OrderBy(e => e.Evidence.SourceId))
            await Authorize(end.Evidence.SourceId, end.Evidence.RetrievedAtUtc, d.Purpose, d.Context, await QualityPersistence.Now(db, token), token);
    }
    public async Task<bool> VerifyFrozenAsync(ResultDatasetGovernance governance, CancellationToken token = default)
    {
        if (governance.Version != 1) return false;
        foreach (var item in governance.Rows.SelectMany(r => r.FeatureCoverage.Items.Concat(r.LabelCoverage.Items)).DistinctBy(i => i.Evidence.Id))
        {
            var e = await db.ResultInventory.AsNoTracking().SingleOrDefaultAsync(e => e.Id == item.Evidence.Id, token);
            if (e is null || CanonicalDatasetJson.Fingerprint(e) != CanonicalDatasetJson.Fingerprint(item.Evidence)) return false;
            if (item.Review is { } review && CanonicalDatasetJson.Fingerprint(await db.ResultInventoryReviews.AsNoTracking().SingleOrDefaultAsync(r => r.Id == review.Id, token)) != CanonicalDatasetJson.Fingerprint(review)) return false;
        }
        foreach (var end in governance.Rows.SelectMany(r => r.Ends).DistinctBy(e => e.Evidence.Id))
            if (CanonicalDatasetJson.Fingerprint(await db.EventEnds.AsNoTracking().SingleOrDefaultAsync(e => e.Id == end.Evidence.Id, token)) != CanonicalDatasetJson.Fingerprint(end.Evidence)) return false;
        return true;
    }
}
