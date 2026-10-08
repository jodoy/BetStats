using System.Data;
using System.Text;
using BetStats.Application.Coverage;
using BetStats.Application.Datasets;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Application.Quality;
using BetStats.Domain.Coverage;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Coverage;

public sealed class HistoricalCoverage(BetStatsDbContext db, IRawPayloadStore store, IFootballMetadataParser parser,
    IAnalyticalQualityGate gate, ISourcePolicyEvaluator policies, ISourceOperationalStatus sources) : IHistoricalCoverage
{
    private const string Contract = "project-owned-fixture-inventory-v1";
    private static object Policy(SourcePolicy policy) => new { policy.Id, policy.DataSourceId, policy.Version, policy.EffectiveFromUtc, policy.EffectiveToUtc,
        policy.TermsReference, policy.EvidenceReference, policy.RecordedAtUtc, Permissions = policy.Permissions.OrderBy(p => p.Purpose).ToArray() };
    private static void Utc(DateTime time) { if (time.Kind != DateTimeKind.Utc || time.Ticks % 10 != 0) throw new ArgumentException("UTC microseconds required."); }
    private static void Text(string value) { if (string.IsNullOrWhiteSpace(value) || value.Length > 1000) throw new ArgumentException("Bounded evidence reference required."); }
    private async Task<Guid> Authorize(Guid source, DataPurpose purpose, UsageContext context, DateTime at, CancellationToken token)
    {
        if (await sources.ReadAsync(source, token) != SourceOperationalStatus.Enabled) throw new UnauthorizedAccessException("Source not enabled.");
        Guid? policy = null;
        foreach (var p in new[] { purpose, DataPurpose.InternalAnalytics, DataPurpose.HistoricalRetention }.Distinct())
        {
            var result = await policies.EvaluateAsync(source, p, at, context, token);
            if (!result.Allowed) throw new UnauthorizedAccessException("Source policy denies evidence use."); policy = result.PolicyId;
        }
        return policy!.Value;
    }
    private async Task<ReadOnlyMemory<byte>> Read(RawPayload raw, CancellationToken token)
    {
        var now = await QualityPersistence.Now(db, token); await Authorize(raw.DataSourceId, DataPurpose.InternalAnalytics, new(), now, token);
        var retention = await policies.EvaluateAsync(raw.DataSourceId, DataPurpose.HistoricalRetention, now, new(), token);
        if (retention.Restrictions.Any(r => r.MaximumRetentionDays is { } days && now - raw.RetrievedAtUtc > TimeSpan.FromDays(days))) throw new UnauthorizedAccessException("Retention age exceeded.");
        return await store.ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, raw.ByteLength ?? throw new InvalidDataException("RAW length missing.")), token);
    }
    public async Task<CoverageEvidence> RecordAsync(CoverageSubmission request, CancellationToken token = default)
    {
        request.Scope.Validate(); Text(request.EvidenceReference); QualityPersistence.Operator(request.OperatorId, request.Reason);
        Utc(request.AvailableUtc); Utc(request.ValidUntilUtc); if (request.PublicationUtc is { } published) Utc(published);
        if (request.Claim is not (CoverageStatus.Unknown or CoverageStatus.Partial or CoverageStatus.VerifiedComplete or CoverageStatus.VerifiedEmpty) ||
            !Enum.IsDefined(request.Basis) || request.Version < 1 || request.ValidUntilUtc <= request.AvailableUtc) throw new ArgumentException("Explicit coverage claim/basis/validity required.");
        var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == request.RawId && r.DataSourceId == request.Scope.SourceId, token);
        var bytes = await Read(raw, token); Guid[] inventory = [];
        if (request.Basis == CoverageBasis.OwnedFixtureInventory)
        {
            var proof = CanonicalDatasetJson.Deserialize<CoverageInventory>(bytes.ToArray());
            if (proof.Contract != Contract || proof.Scope != request.Scope || proof.Claim != request.Claim || proof.ObservationIds.Count > 1000 || proof.ObservationIds.Distinct().Count() != proof.ObservationIds.Count)
                throw new InvalidDataException("Owned inventory contract mismatch.");
            inventory = proof.ObservationIds.Order().ToArray();
            if (request.Claim == CoverageStatus.VerifiedEmpty && inventory.Length != 0 || request.Claim == CoverageStatus.VerifiedComplete && inventory.Length == 0)
                throw new InvalidDataException("Complete/empty inventory mismatch.");
        }
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await QualityPersistence.Lock(db, request.Scope.SourceId, token); var now = await QualityPersistence.Now(db, token);
        var policy = await Authorize(request.Scope.SourceId, DataPurpose.InternalAnalytics, new(), now, token);
        if (request.AvailableUtc < raw.RetrievedAtUtc || request.AvailableUtc > now || request.PublicationUtc > request.AvailableUtc) throw new ArgumentException("Evidence availability cannot be invented.");
        var evidence = new CoverageEvidence { Id = Guid.NewGuid(), SourceId = request.Scope.SourceId, Scope = request.Scope, Claim = request.Claim, Basis = request.Basis,
            EvidenceReference = request.EvidenceReference, RawId = raw.Id, RawHash = raw.ContentHashSha256, PolicyId = policy, SupportingObservationIds = inventory,
            Version = request.Version, SourcePublishedAtUtc = request.PublicationUtc, RetrievedAtUtc = raw.RetrievedAtUtc, AvailableAtUtc = request.AvailableUtc,
            ValidUntilUtc = request.ValidUntilUtc, OperatorId = request.OperatorId, Reason = request.Reason };
        db.CoverageEvidence.Add(evidence); await db.SaveChangesAsync(token); await tx.CommitAsync(token); return evidence;
    }
    public async Task<CoverageReview> ReviewAsync(CoverageReviewRequest request, CancellationToken token = default)
    {
        QualityPersistence.Operator(request.OperatorId, request.Reason); Text(request.BasisReference);
        if (!Enum.IsDefined(request.Status) || request.ExpectedSequence < 0) throw new ArgumentException("Review status/version required.");
        var evidence = await db.CoverageEvidence.AsNoTracking().SingleAsync(e => e.Id == request.EvidenceId, token);
        // Validate supporting bytes before short source lock. The exact verified bytes/hash are immutable.
        var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == evidence.RawId, token); await Read(raw, token);
        var now = await QualityPersistence.Now(db, token);
        var strong = evidence.Claim is CoverageStatus.VerifiedComplete or CoverageStatus.VerifiedEmpty;
        var verified = !strong || evidence.Basis == CoverageBasis.OwnedFixtureInventory && request.BasisReference == Contract &&
            await InventoryValid(evidence, now, DatasetMode.HistoricalAsKnown, null, DataPurpose.InternalAnalytics, new(), token);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await QualityPersistence.Lock(db, evidence.SourceId, token); now = await QualityPersistence.Now(db, token); await Authorize(evidence.SourceId, DataPurpose.InternalAnalytics, new(), now, token);
        var sequence = await db.CoverageReviews.Where(r => r.EvidenceId == evidence.Id).Select(r => (int?)r.Sequence).MaxAsync(token) ?? 0;
        if (sequence != request.ExpectedSequence) throw new InvalidOperationException("Concurrent coverage review conflict.");
        if (evidence.ValidUntilUtc <= now) verified = false;
        var review = new CoverageReview { Id = Guid.NewGuid(), EvidenceId = evidence.Id, SourceId = evidence.SourceId, Sequence = sequence + 1,
            Status = request.Status == CoverageReviewStatus.Approved && !verified ? CoverageReviewStatus.Rejected : request.Status, BasisReference = request.BasisReference,
            OperatorId = request.OperatorId, Reason = request.Reason, ReviewedAtUtc = now };
        db.CoverageReviews.Add(review); await db.SaveChangesAsync(token); await tx.CommitAsync(token); return review;
    }
    private async Task<bool> Matches(Observation observation, CoverageScope scope, DateTime cutoff, DateTime knowledge, DatasetMode mode, DateTime? reconstruction,
        DataPurpose purpose, UsageContext context, CancellationToken token)
    {
        if (observation.DataSourceId != scope.SourceId || observation.Type != scope.ObservationType) return false;
        var eligibility = await gate.EvaluateAsync(new(observation.Id, cutoff, purpose, context, mode, reconstruction), token);
        if (!eligibility.Eligible) return false;
        var identity = await db.ProviderIdentities.AsNoTracking().SingleAsync(i => i.Id == observation.ProviderIdentityId, token);
        var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == observation.RawPayloadId, token);
        var parsed = parser.Parse(await Read(raw, token), new(scope.CompetitionReference, scope.SeasonReference), token);
        var row = parsed.Records.SingleOrDefault(r => r.MatchReference == identity.ExternalId); if (row is null) return false;
        if (scope.Interval.Kind != IntervalKind.Calendar || scope.Interval.CalendarBasis != "UTC-calendar") return false;
        if (row.MatchDate < scope.Interval.StartDate || row.MatchDate >= scope.Interval.EndDate) return false;
        foreach (var pair in new[] { (CanonicalEntityKind.Competition, "provider:competition:" + row.CompetitionReference, (Guid?)scope.CompetitionId),
            (CanonicalEntityKind.Season, FootballDataCsvParser.SeasonReference(row.CompetitionReference, row.SeasonReference), (Guid?)scope.SeasonId) })
        {
            var anchor = await QualityPersistence.Anchor(db, scope.SourceId, pair.Item1, pair.Item2, token);
            if ((await QualityPersistence.Decision(db, anchor?.Id, knowledge, token))?.CanonicalId != pair.Item3) return false;
        }
        if (scope.ParticipantId is { } participant)
        {
            var matches = false;
            foreach (var reference in new[] { row.HomeReference, row.AwayReference })
            {
                var anchor = await QualityPersistence.Anchor(db, scope.SourceId, CanonicalEntityKind.Participant, reference, token);
                if ((await QualityPersistence.Decision(db, anchor?.Id, knowledge, token))?.CanonicalParticipantId == participant) matches = true;
            }
            if (!matches) return false;
        }
        return scope.SportId == FootballQualityRules.Football;
    }
    private async Task<bool> InventoryValid(CoverageEvidence evidence, DateTime cutoff, DatasetMode mode, DateTime? reconstruction, DataPurpose purpose, UsageContext context, CancellationToken token)
    {
        var sourcePolicy = await db.SourcePolicies.AsNoTracking().SingleAsync(p => p.Id == evidence.PolicyId, token);
        if (sourcePolicy.TermsReference != "synthetic:owned-fixture" || evidence.Scope.Interval.Kind != IntervalKind.Calendar || evidence.Scope.Interval.CalendarBasis != "UTC-calendar") return false;
        var observations = await db.Observations.AsNoTracking().Where(o => o.DataSourceId == evidence.SourceId && o.Type == evidence.Scope.ObservationType &&
            o.AvailableAtUtc <= cutoff && o.RecordedAtUtc <= cutoff && !db.Observations.Any(n => n.ProviderIdentityId == o.ProviderIdentityId && n.Type == o.Type && n.Version > o.Version && n.AvailableAtUtc <= cutoff && n.RecordedAtUtc <= cutoff))
            .OrderBy(o => o.Id).Take(1001).ToListAsync(token);
        if (observations.Count > 1000) throw new InvalidOperationException("Inventory bound exceeded.");
        var ids = new List<Guid>(); var knowledge = reconstruction ?? cutoff;
        foreach (var o in observations)
        {
            // Determine potential scope independently of quality. A failed quality/identity gate
            // cannot turn an observed event into affirmative evidence of an empty interval.
            var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == o.RawPayloadId, token);
            var anchor = await db.ProviderIdentities.AsNoTracking().SingleAsync(i => i.Id == o.ProviderIdentityId, token);
            var parsed = parser.Parse(await Read(raw, token), new(evidence.Scope.CompetitionReference, evidence.Scope.SeasonReference), token);
            var row = parsed.Records.SingleOrDefault(r => r.MatchReference == anchor.ExternalId);
            if (row is null) return false;
            if (row.MatchDate < evidence.Scope.Interval.StartDate || row.MatchDate >= evidence.Scope.Interval.EndDate) continue;
            if (evidence.Scope.ParticipantId is { } participant)
            {
                var participants = new List<Guid>();
                foreach (var reference in new[] { row.HomeReference, row.AwayReference })
                {
                    var identity = await QualityPersistence.Anchor(db, evidence.SourceId, CanonicalEntityKind.Participant, reference, token);
                    var decision = await QualityPersistence.Decision(db, identity?.Id, knowledge, token);
                    if (decision?.CanonicalParticipantId is not { } id || decision.Status != ResolutionStatus.Resolved) return false;
                    participants.Add(id);
                }
                if (!participants.Contains(participant)) continue;
            }
            if (!await Matches(o, evidence.Scope, cutoff, knowledge, mode, reconstruction, purpose, context, token)) return false;
            ids.Add(o.Id);
        }
        return ids.Order().SequenceEqual(evidence.SupportingObservationIds.Order());
    }
    public async Task<CoverageEvidence> InspectAsync(Guid id, CancellationToken token = default)
    {
        var e = await db.CoverageEvidence.AsNoTracking().SingleAsync(e => e.Id == id, token);
        await Authorize(e.SourceId, DataPurpose.InternalAnalytics, new(), await QualityPersistence.Now(db, token), token); return e;
    }
    public async Task<CoverageReport> ReportAsync(CoverageQuery query, CancellationToken token = default)
    {
        query.Scope.Validate(); Utc(query.AsOfUtc); var knowledge = query.ReconstructionUtc ?? query.AsOfUtc; Utc(knowledge);
        if (!Enum.IsDefined(query.Mode) || query.Mode == DatasetMode.HistoricalAsKnown && query.ReconstructionUtc is not null ||
            query.Mode == DatasetMode.RetrospectiveReconstruction && (query.ReconstructionUtc is null || knowledge < query.AsOfUtc) || knowledge > await QualityPersistence.Now(db, token))
            throw new ArgumentException("Explicit historical/reconstruction bounds required.");
        try { await Authorize(query.Scope.SourceId, query.Purpose, query.Context, knowledge, token); await Authorize(query.Scope.SourceId, query.Purpose, query.Context, await QualityPersistence.Now(db, token), token); }
        catch (UnauthorizedAccessException) { return new(query, CoverageStatus.Unknown, false, [], [query.Scope.Interval], [], [], [query.Scope.ObservationType], ["source_permission_denied"]); }
        var candidates = await db.CoverageEvidence.AsNoTracking().Where(e => e.SourceId == query.Scope.SourceId && e.AvailableAtUtc <= query.AsOfUtc && e.RecordedAtUtc <= query.AsOfUtc)
            .OrderBy(e => e.Id).Take(201).ToListAsync(token);
        if (candidates.Count > 200) throw new InvalidOperationException("Coverage query bound exceeded.");
        var items = new List<CoverageItem>(); var now = await QualityPersistence.Now(db, token);
        foreach (var e in candidates.Where(e => e.Scope.SameDimensions(query.Scope) && e.Scope.Interval.Compatible(query.Scope.Interval) && CoverageRules.Intersection(e.Scope.Interval, query.Scope.Interval) is not null))
        {
            var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == e.RawId, token);
            if (raw.RecordedAtUtc > query.AsOfUtc || raw.ContentHashSha256 != e.RawHash) continue;
            var review = await db.CoverageReviews.AsNoTracking().Where(r => r.EvidenceId == e.Id && r.RecordedAtUtc <= knowledge && r.ReviewedAtUtc <= knowledge).OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(token);
            var status = review?.Status != CoverageReviewStatus.Approved ? CoverageStatus.Unknown : e.Claim;
            var reasons = new List<string>(); var ids = new List<Guid>(); var quality = new List<Guid>();
            if (e.ValidUntilUtc <= knowledge || e.ValidUntilUtc <= now) { status = CoverageStatus.Expired; reasons.Add("coverage_validity_expired"); }
            if (status is CoverageStatus.VerifiedComplete or CoverageStatus.VerifiedEmpty && !await InventoryValid(e, query.AsOfUtc, query.Mode, query.ReconstructionUtc, query.Purpose, query.Context, token))
            { status = CoverageStatus.Conflicting; reasons.Add("inventory_no_longer_matches_eligible_historical_evidence"); }
            foreach (var observationId in e.SupportingObservationIds)
            {
                var result = await gate.EvaluateAsync(new(observationId, query.AsOfUtc, query.Purpose, query.Context, query.Mode, query.ReconstructionUtc), token);
                if (result.DecisionId is { } decision) ids.Add(decision); quality.AddRange(result.AssessmentIds);
            }
            items.Add(new(e, review, status, ids.Distinct().Order().ToArray(), quality.Distinct().Order().ToArray(), reasons));
        }
        var classification = CoverageRules.Classify(query.Scope.Interval, items);
        var strong = items.Where(i => i.Status is CoverageStatus.VerifiedComplete or CoverageStatus.VerifiedEmpty).Select(i => i.Evidence.Scope.Interval);
        var conflicts = new List<CoverageInterval>();
        for (var i = 0; i < items.Count; i++) for (var j = i + 1; j < items.Count; j++)
            if (items[i].Status is CoverageStatus.VerifiedComplete or CoverageStatus.VerifiedEmpty && items[j].Status is CoverageStatus.VerifiedComplete or CoverageStatus.VerifiedEmpty &&
                (items[i].Status != items[j].Status || !items[i].Evidence.SupportingObservationIds.SequenceEqual(items[j].Evidence.SupportingObservationIds)) &&
                CoverageRules.Intersection(items[i].Evidence.Scope.Interval, items[j].Evidence.Scope.Interval) is { } overlap) conflicts.Add(overlap);
        conflicts.AddRange(items.Where(i => i.Status == CoverageStatus.Conflicting).Select(i => CoverageRules.Intersection(i.Evidence.Scope.Interval, query.Scope.Interval)!));
        return new(query, classification, true, items, CoverageRules.Gaps(query.Scope.Interval, strong),
            items.Where(i => i.Status == CoverageStatus.Partial).Select(i => CoverageRules.Intersection(i.Evidence.Scope.Interval, query.Scope.Interval)!).ToArray(),
            conflicts.Distinct().OrderBy(i => i.Start).ThenBy(i => i.End).ToArray(), items.Count == 0 ? [query.Scope.ObservationType] : [],
            query.Scope.Interval.CalendarBasis == "unknown" ? ["calendar_basis_unknown"] : []);
    }
    public async Task<EventTimeEvidence> RecordTimeAsync(EventTimeSubmission request, CancellationToken token = default)
    {
        Text(request.EvidenceReference); QualityPersistence.Operator(request.OperatorId, request.Reason); Utc(request.AvailableUtc); if (request.PublicationUtc is { } p) Utc(p);
        EventTimeRules.Resolve(request.Value);
        var observation = await db.Observations.AsNoTracking().SingleAsync(o => o.Id == request.DateObservationId && o.Type == ObservationType.EventDate, token);
        var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == request.RawId && r.DataSourceId == observation.DataSourceId, token);
        var bytes = await Read(raw, token);
        if (request.Value.Precision == EventTimePrecision.DateOnly)
        {
            if (raw.Id != observation.RawPayloadId || request.Value.LocalDate != observation.DateValue) throw new InvalidDataException("Date-only provenance mismatch.");
        }
        else
        {
            var supplied = CanonicalDatasetJson.Deserialize<EventTimeValue>(bytes.ToArray());
            if (supplied != request.Value) throw new InvalidDataException("Event-time RAW claim mismatch.");
        }
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token); await QualityPersistence.Lock(db, observation.DataSourceId, token);
        var now = await QualityPersistence.Now(db, token); var policy = await Authorize(observation.DataSourceId, DataPurpose.InternalAnalytics, new(), now, token);
        if (request.AvailableUtc < raw.RetrievedAtUtc || request.AvailableUtc > now || request.PublicationUtc > request.AvailableUtc) throw new ArgumentException("Invalid event-time availability.");
        var previous = request.CorrectsId is { } prev ? await db.EventTimeEvidence.AsNoTracking().SingleAsync(e => e.Id == prev, token) : null;
        if (previous is not null && (previous.ProviderIdentityId != observation.ProviderIdentityId || previous.SourceId != raw.DataSourceId || request.AvailableUtc < previous.AvailableAtUtc))
            throw new ArgumentException("Explicit same-stream nonbackdated correction required.");
        var result = new EventTimeEvidence { Id = Guid.NewGuid(), SourceId = raw.DataSourceId, ProviderIdentityId = observation.ProviderIdentityId, DateObservationId = observation.Id,
            RawId = raw.Id, RawHash = raw.ContentHashSha256, PolicyId = policy, Value = request.Value, EvidenceReference = request.EvidenceReference, CorrectsId = previous?.Id,
            Version = previous is null ? 1 : checked(previous.Version + 1), SourcePublishedAtUtc = request.PublicationUtc, RetrievedAtUtc = raw.RetrievedAtUtc,
            AvailableAtUtc = request.AvailableUtc, OperatorId = request.OperatorId, Reason = request.Reason };
        db.EventTimeEvidence.Add(result); await db.SaveChangesAsync(token); await tx.CommitAsync(token); return result;
    }
    public async Task<IReadOnlyList<EventTimeClaimResult>> TimesAsync(Guid identityId, DateTime asOf, DatasetMode mode, DateTime? reconstruction, DataPurpose purpose, UsageContext context, CancellationToken token = default)
    {
        Utc(asOf); var knowledge = reconstruction ?? asOf; Utc(knowledge);
        if (!Enum.IsDefined(mode) || mode == DatasetMode.HistoricalAsKnown && reconstruction is not null || mode == DatasetMode.RetrospectiveReconstruction && (reconstruction is null || knowledge < asOf) || knowledge > await QualityPersistence.Now(db, token)) throw new ArgumentException("Explicit time knowledge boundary required.");
        var identity = await db.ProviderIdentities.AsNoTracking().SingleAsync(i => i.Id == identityId, token);
        await Authorize(identity.DataSourceId, purpose, context, knowledge, token); await Authorize(identity.DataSourceId, purpose, context, await QualityPersistence.Now(db, token), token);
        var decision = await QualityPersistence.Decision(db, identityId, knowledge, token); if (decision?.CanonicalSportingEventId is not { } eventId) return [];
        var claims = await db.EventTimeEvidence.AsNoTracking().Where(e => e.ProviderIdentityId == identityId && e.AvailableAtUtc <= asOf && e.RecordedAtUtc <= asOf &&
            !db.EventTimeEvidence.Any(n => n.CorrectsId == e.Id && n.AvailableAtUtc <= asOf && n.RecordedAtUtc <= asOf)).OrderBy(e => e.Id).Take(201).ToListAsync(token);
        if (claims.Count > 200) throw new InvalidOperationException("Event-time bound exceeded.");
        var results = new List<EventTimeClaimResult>();
        foreach (var e in claims)
        {
            var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == e.RawId, token);
            if (raw.RecordedAtUtc > asOf || raw.ContentHashSha256 != e.RawHash) continue;
            if (!(await gate.EvaluateAsync(new(e.DateObservationId, asOf, purpose, context, mode, reconstruction), token)).Eligible) continue;
            var resolution = EventTimeRules.Resolve(e.Value);
            if (claims.Any(other => other.Id != e.Id && EventTimeRules.Conflicts(e.Value, other.Value))) resolution = new(null, e.Value.Precision, "conflicting_event_time_claims");
            results.Add(new(e, decision.Id, eventId, resolution));
        }
        return results;
    }
    public async Task<DatasetGovernanceRow> DatasetRowAsync(DatasetDefinition definition, DatasetRow row, CancellationToken token = default)
    {
        var reports = new List<CoverageReport>(); var gates = new List<FeatureCoverageDecision>();
        foreach (var feature in FootballMetadataFeatures.Catalog)
        {
            var participant = feature.Name.StartsWith("home", StringComparison.Ordinal) ? row.Target.HomeId : row.Target.AwayId;
            var day = DateOnly.FromDateTime(row.PredictionCutoffUtc);
            var start = feature.LookbackDays is { } lookback ? day.AddDays(-lookback) : definition.SeasonStart;
            if (start >= day) start = day.AddDays(-1);
            var selected = new List<CoverageReport>();
            foreach (var type in new[] { ObservationType.EventDate, ObservationType.EventStatus })
            {
                var scope = new CoverageScope(row.Target.SourceId, definition.SportId, definition.CompetitionId, definition.SeasonId, participant, "football-match", type,
                    definition.CompetitionReference, definition.SeasonReference, new(IntervalKind.Calendar, null, null, start, day, definition.CalendarBasis));
                var report = await ReportAsync(new(scope, row.PredictionCutoffUtc, definition.Mode, definition.ReconstructionAtUtc, definition.Purpose, definition.Context), token);
                reports.Add(report); selected.Add(report);
            }
            gates.Add(CoverageRules.Gate(FootballMetadataFeatures.Requirement(feature), selected));
        }
        var times = await TimesAsync(row.Target.ProviderIdentityId, row.PredictionCutoffUtc, definition.Mode, definition.ReconstructionAtUtc, definition.Purpose, definition.Context, token);
        var frozen = new List<DatasetFrozenRecord>();
        void Freeze<T>(string kind, Guid id, T value) => frozen.Add(new(kind, id, Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(value))));
        foreach (var item in reports.SelectMany(r => r.Items).DistinctBy(i => i.Evidence.Id))
        { Freeze("coverage", item.Evidence.Id, item.Evidence); if (item.Review is not null) Freeze("coverage-review", item.Review.Id, item.Review); }
        foreach (var time in times) Freeze("event-time", time.Evidence.Id, time.Evidence);
        foreach (var id in reports.SelectMany(r => r.Items).SelectMany(i => i.IdentityDecisionIds).Concat(times.Select(t => t.IdentityDecisionId)).Distinct().Order())
            Freeze("decision", id, await db.IdentityResolutions.AsNoTracking().SingleAsync(v => v.Id == id, token));
        foreach (var id in reports.SelectMany(r => r.Items).SelectMany(i => i.QualityIds).Distinct().Order())
            Freeze("quality", id, await db.QualityAssessments.AsNoTracking().SingleAsync(v => v.Id == id, token));
        foreach (var id in reports.SelectMany(r => r.Items).Select(i => i.Evidence.PolicyId).Concat(times.Select(t => t.Evidence.PolicyId)).Distinct().Order())
            Freeze("coverage-policy", id, Policy(await db.SourcePolicies.AsNoTracking().Include(p => p.Permissions).SingleAsync(v => v.Id == id, token)));
        var rawIds = reports.SelectMany(r => r.Items).Select(i => i.Evidence.RawId).Concat(times.Select(t => t.Evidence.RawId)).Distinct().Order().ToArray();
        foreach (var id in rawIds) Freeze("raw", id, await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == id, token));
        return new(row.EventId, row.PredictionCutoffUtc, 1, reports, gates, times, new(null, EventTimePrecision.DateOnly, "date_only_observation_no_kickoff"),
            frozen.OrderBy(f => f.Kind, StringComparer.Ordinal).ThenBy(f => f.Id).ToArray());
    }
    public async Task<bool> VerifyFrozenAsync(DatasetGovernance governance, CancellationToken token = default)
    {
        foreach (var frozen in governance.Rows.SelectMany(r => r.FrozenRecords).DistinctBy(f => (f.Kind, f.Id)))
        {
            object? value = frozen.Kind switch {
                "coverage" => await db.CoverageEvidence.AsNoTracking().SingleOrDefaultAsync(e => e.Id == frozen.Id, token),
                "coverage-review" => await db.CoverageReviews.AsNoTracking().SingleOrDefaultAsync(e => e.Id == frozen.Id, token),
                "event-time" => await db.EventTimeEvidence.AsNoTracking().SingleOrDefaultAsync(e => e.Id == frozen.Id, token),
                "decision" => await db.IdentityResolutions.AsNoTracking().SingleOrDefaultAsync(e => e.Id == frozen.Id, token),
                "quality" => await db.QualityAssessments.AsNoTracking().SingleOrDefaultAsync(e => e.Id == frozen.Id, token),
                "coverage-policy" => await db.SourcePolicies.AsNoTracking().Include(p => p.Permissions).SingleOrDefaultAsync(e => e.Id == frozen.Id, token) is { } p ? Policy(p) : null,
                "raw" => await db.RawPayloads.AsNoTracking().SingleOrDefaultAsync(e => e.Id == frozen.Id, token), _ => null };
            if (value is null || Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(value)) != frozen.CanonicalJson) return false;
        }
        return true;
    }
    public async Task EnsureCurrentAsync(DatasetGovernance governance, DatasetDefinition definition, CancellationToken token = default)
    {
        var now = await QualityPersistence.Now(db, token);
        foreach (var item in governance.Rows.SelectMany(r => r.Coverage).SelectMany(r => r.Items).DistinctBy(i => i.Evidence.Id))
        {
            await Authorize(item.Evidence.SourceId, definition.Purpose, definition.Context, now, token);
            if (item.Status is CoverageStatus.VerifiedComplete or CoverageStatus.VerifiedEmpty && item.Evidence.ValidUntilUtc <= now) throw new UnauthorizedAccessException("Coverage expired before finalization/use.");
        }
        foreach (var rawId in governance.Rows.SelectMany(r => r.FrozenRecords).Where(f => f.Kind == "raw").Select(f => f.Id).Distinct().Order())
        {
            var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == rawId, token);
            await Authorize(raw.DataSourceId, definition.Purpose, definition.Context, now, token);
            var retention = await policies.EvaluateAsync(raw.DataSourceId, DataPurpose.HistoricalRetention, now, definition.Context, token);
            if (retention.Restrictions.Any(r => r.MaximumRetentionDays is { } days && now - raw.RetrievedAtUtc > TimeSpan.FromDays(days)))
                throw new UnauthorizedAccessException("Coverage/time evidence retention age exceeded.");
        }
    }
}
