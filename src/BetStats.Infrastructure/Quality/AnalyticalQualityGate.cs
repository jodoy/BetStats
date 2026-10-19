using BetStats.Application.Governance;
using BetStats.Application.Providers;
using BetStats.Application.Quality;
using BetStats.Domain.Governance;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using BetStats.Domain.Identity;
using BetStats.Infrastructure.Persistence.Entities;

namespace BetStats.Infrastructure.Quality;

public sealed class AnalyticalQualityGate(BetStatsDbContext db, ISourcePolicyEvaluator policies, ISourceOperationalStatus sources) : IAnalyticalQualityGate
{
    private sealed class Batch
    {
        public required DateTime Now { get; init; }
        public required Dictionary<Guid, Observation> Observations { get; init; }
        public required Dictionary<Guid, RawPayload> Raw { get; init; }
        public required Dictionary<Guid, ProviderIdentity> Anchors { get; init; }
        public required Dictionary<Guid, IdentityResolution> Decisions { get; init; }
        public required HashSet<Guid> Superseded { get; init; }
        public required ILookup<Guid, QualityAssessment> Assessments { get; init; }
        public required ILookup<Guid, Observation> Others { get; init; }
        public Dictionary<Guid, IReadOnlyDictionary<DataPurpose, PolicyEvaluation>> Historical { get; } = [];
        public Dictionary<Guid, IReadOnlyDictionary<DataPurpose, PolicyEvaluation>> Current { get; } = [];
        public Dictionary<Guid, SourceOperationalStatus> Status { get; } = [];
    }
    public Task<QualityGateResult> EvaluateAsync(EligibilityQuery query, CancellationToken token = default) => EvaluateCoreAsync(query, token);
    private static DateTime Validate(EligibilityQuery query)
    {
        void Utc(DateTime at) { if (at.Kind != DateTimeKind.Utc || at.Ticks % 10 != 0) throw new ArgumentException("UTC microsecond precision required."); }
        Utc(query.AsOfUtc);
        if (!Enum.IsDefined(query.Mode) || !Enum.IsDefined(query.Purpose) || query.ObservationId == Guid.Empty || query.Context is null) throw new ArgumentException("Valid eligibility query required.");
        if (query.Purpose is not (DataPurpose.InternalAnalytics or DataPurpose.ModelTraining or DataPurpose.PublicDisplay or DataPurpose.CommercialUse or DataPurpose.Redistribution))
            throw new ArgumentException("A dataset usage purpose is required; retrieval/storage rights are not analytical rights.");
        if (query.Mode == DatasetMode.HistoricalAsKnown && query.ReconstructionAtUtc is not null ||
            query.Mode == DatasetMode.RetrospectiveReconstruction && (query.ReconstructionAtUtc is null || query.ReconstructionAtUtc < query.AsOfUtc))
            throw new ArgumentException("Reconstruction mode requires a distinct timestamp at or after the evidence cutoff.");
        var knowledge = query.ReconstructionAtUtc ?? query.AsOfUtc; Utc(knowledge);
        return knowledge;
    }
    public async Task<IReadOnlyList<QualityGateResult>> EvaluateManyAsync(IReadOnlyList<EligibilityQuery> queries, CancellationToken token = default)
    {
        if (queries.Count > 100) throw new ArgumentException("Eligibility batch exceeds 100.");
        foreach (var query in queries) Validate(query);
        var output = new QualityGateResult[queries.Count];
        foreach (var group in queries.Select((query, index) => (query, index)).GroupBy(x =>
            (x.query.AsOfUtc, x.query.ReconstructionAtUtc, x.query.Mode, x.query.Purpose, x.query.Context)))
        {
            var q = group.First().query; var knowledge = Validate(q);
            var now = await QualityPersistence.Now(db, token);
            if (knowledge > now) throw new ArgumentException("Future knowledge timestamps are not supported.");
            var ids = group.Select(x => x.query.ObservationId).Distinct().ToArray();
            var observations = await db.Observations.AsNoTracking().Where(o => ids.Contains(o.Id)).ToDictionaryAsync(o => o.Id, token);
            var identityIds = observations.Values.Select(o => o.ProviderIdentityId).Distinct().ToArray();
            var rawIds = observations.Values.Where(o => o.RawPayloadId != null).Select(o => o.RawPayloadId!.Value).Distinct().ToArray();
            var anchors = await db.ProviderIdentities.AsNoTracking().Where(i => identityIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, token);
            var raw = await db.RawPayloads.AsNoTracking().Where(r => rawIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, token);
            var decisions = await db.IdentityResolutions.AsNoTracking().Where(d => identityIds.Contains(d.ProviderIdentityId) && d.RecordedAtUtc <= knowledge && d.DecidedAtUtc <= knowledge)
                .GroupBy(d => d.ProviderIdentityId).Select(g => g.OrderByDescending(d => d.Version).First()).ToListAsync(token);
            var superseded = await db.Observations.AsNoTracking().Where(o => ids.Contains(o.Id) && db.Observations.Any(n =>
                n.ProviderIdentityId == o.ProviderIdentityId && n.Type == o.Type && n.Version > o.Version && n.AvailableAtUtc <= q.AsOfUtc && n.RecordedAtUtc <= q.AsOfUtc))
                .Select(o => o.Id).ToListAsync(token);
            // Correlated Take applies the original limit to each observation,
            // rather than to the batch as a whole. EF translates to LATERAL.
            var assessments = await (from o in db.Observations.AsNoTracking()
                where ids.Contains(o.Id)
                join anchor in db.ProviderIdentities.AsNoTracking() on o.ProviderIdentityId equals anchor.Id
                from a in db.QualityAssessments.AsNoTracking().Where(a => a.DataSourceId == o.DataSourceId && a.RawPayloadId == o.RawPayloadId &&
                    (a.ProviderIdentityId == o.ProviderIdentityId || a.RecordReference == anchor.ExternalId) && a.AssessedAtUtc <= knowledge && a.RecordedAtUtc <= knowledge)
                    .OrderByDescending(a => a.RecordedAtUtc).ThenByDescending(a => a.Id).Take(1001)
                select new { ObservationId = o.Id, Assessment = a }).ToListAsync(token);
            var others = await (from o in db.Observations.AsNoTracking()
                where ids.Contains(o.Id) && (o.CanonicalSportId != null || o.CanonicalCompetitionId != null || o.CanonicalSeasonId != null || o.CanonicalParticipantId != null || o.CanonicalSportingEventId != null)
                from n in db.Observations.AsNoTracking().Where(n => n.DataSourceId != o.DataSourceId && n.Type == o.Type &&
                    n.CanonicalSportingEventId == o.CanonicalSportingEventId && n.CanonicalParticipantId == o.CanonicalParticipantId &&
                    n.AvailableAtUtc <= q.AsOfUtc && n.RecordedAtUtc <= q.AsOfUtc && !db.Observations.Any(p => p.ProviderIdentityId == n.ProviderIdentityId &&
                        p.Type == n.Type && p.Version > n.Version && p.AvailableAtUtc <= q.AsOfUtc && p.RecordedAtUtc <= q.AsOfUtc))
                    .OrderBy(n => n.Id).Take(101)
                select new { ObservationId = o.Id, Other = n }).ToListAsync(token);
            var batch = new Batch { Now = now, Observations = observations, Anchors = anchors, Raw = raw,
                Decisions = decisions.ToDictionary(d => d.ProviderIdentityId), Superseded = superseded.ToHashSet(),
                Assessments = assessments.ToLookup(a => a.ObservationId, a => a.Assessment), Others = others.ToLookup(n => n.ObservationId, n => n.Other) };
            var purposes = new[] { q.Purpose, DataPurpose.InternalAnalytics, DataPurpose.HistoricalRetention }.Distinct().ToArray();
            foreach (var source in observations.Values.Select(o => o.DataSourceId).Distinct())
            {
                batch.Historical.Add(source, await policies.EvaluateManyAsync(source, purposes, knowledge, q.Context, token));
                batch.Current.Add(source, await policies.EvaluateManyAsync(source, purposes, now, q.Context, token));
                batch.Status.Add(source, await sources.ReadAsync(source, token));
            }
            foreach (var item in group) output[item.index] = await EvaluateCoreAsync(item.query, token, batch);
            // No partially authorized result escapes the call. Current rights
            // are read again after materialization, with a new database clock.
            var finalNow = await QualityPersistence.Now(db, token);
            foreach (var source in batch.Current.Keys)
            {
                var current = await policies.EvaluateManyAsync(source, purposes, finalNow, q.Context, token);
                var enabled = await sources.ReadAsync(source, token) == SourceOperationalStatus.Enabled;
                foreach (var item in group.Where(x => observations.GetValueOrDefault(x.query.ObservationId)?.DataSourceId == source))
                {
                    var result = output[item.index]; var reasons = new SortedSet<string>(result.Reasons, StringComparer.Ordinal);
                    if (!enabled) reasons.Add("source_not_enabled");
                    var observation = observations[item.query.ObservationId];
                    var payload = observation.RawPayloadId is { } rawId ? raw.GetValueOrDefault(rawId) : null;
                    foreach (var decision in current.Values)
                    {
                        if (!decision.Allowed) reasons.Add("current_policy_" + decision.Reason);
                        if (payload is not null && decision.Restrictions.Any(r => r.MaximumRetentionDays is { } days && finalNow - payload.RetrievedAtUtc > TimeSpan.FromDays(days))) reasons.Add("retention_age_exceeded");
                    }
                    output[item.index] = result with { Eligible = reasons.Count == 0, Reasons = reasons.ToArray() };
                }
            }
        }
        return output;
    }
    private async Task<QualityGateResult> EvaluateCoreAsync(EligibilityQuery query, CancellationToken token, Batch? batch = null)
    {
        var knowledge = Validate(query);
        var now = batch?.Now ?? await QualityPersistence.Now(db, token);
        if (knowledge > now) throw new ArgumentException("Future knowledge timestamps are not supported.");
        var reasons = new SortedSet<string>(StringComparer.Ordinal); var ids = new List<Guid>(); var conflictIds = new List<Guid>();
        var o = batch is null ? await db.Observations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == query.ObservationId, token) : batch.Observations.GetValueOrDefault(query.ObservationId);
        if (o is null) return new(false, query.Mode, query.AsOfUtc, query.ReconstructionAtUtc, query.ObservationId, null, null, null, null, ["observation_missing"], []);
        if (o.AvailableAtUtc > query.AsOfUtc || o.RecordedAtUtc > query.AsOfUtc) reasons.Add("observation_not_known_at_cutoff");
        var decision = batch is null ? await QualityPersistence.Decision(db, o.ProviderIdentityId, knowledge, token) : batch.Decisions.GetValueOrDefault(o.ProviderIdentityId);
        if (decision?.Status != BetStats.Domain.Identity.ResolutionStatus.Resolved) reasons.Add("identity_unresolved_at_knowledge_time");
        if (query.Mode == DatasetMode.HistoricalAsKnown && (o.CanonicalId is null || decision?.CanonicalId != o.CanonicalId)) reasons.Add("frozen_target_mismatch");
        var raw = batch is null ? await db.RawPayloads.AsNoTracking().SingleOrDefaultAsync(r => r.Id == o.RawPayloadId, token) : o.RawPayloadId is { } rawId ? batch.Raw.GetValueOrDefault(rawId) : null;
        if (raw is null || raw.DataSourceId != o.DataSourceId || raw.RecordedAtUtc > query.AsOfUtc || raw.RetrievedAtUtc > o.RetrievedAtUtc) reasons.Add("provenance_incomplete_at_cutoff");
        var superseded = batch is null ? await db.Observations.AnyAsync(n => n.ProviderIdentityId == o.ProviderIdentityId && n.Type == o.Type && n.Version > o.Version &&
            n.AvailableAtUtc <= query.AsOfUtc && n.RecordedAtUtc <= query.AsOfUtc, token) : batch.Superseded.Contains(o.Id);
        if (superseded) reasons.Add("superseded_observation");
        var anchor = batch is null ? await db.ProviderIdentities.AsNoTracking().SingleAsync(i => i.Id == o.ProviderIdentityId, token) : batch.Anchors[o.ProviderIdentityId];
        var assessments = batch is null ? await db.QualityAssessments.AsNoTracking().Where(a => a.DataSourceId == o.DataSourceId && a.RawPayloadId == o.RawPayloadId &&
            (a.ProviderIdentityId == o.ProviderIdentityId || a.RecordReference == anchor.ExternalId) && a.AssessedAtUtc <= knowledge && a.RecordedAtUtc <= knowledge)
            .OrderByDescending(a => a.RecordedAtUtc).ThenByDescending(a => a.Id).Take(1001).ToListAsync(token) : batch.Assessments[o.Id].OrderByDescending(a => a.RecordedAtUtc).ThenByDescending(a => a.Id).ToList();
        if (assessments.Count > 1000) throw new InvalidOperationException("Assessment history exceeds eligibility query bound.");
        var latest = assessments.GroupBy(a => a.RuleId).Select(g => g.First()).ToArray();
        if (latest.Length == 0 || FootballQualityRules.Catalog.Any(r => !latest.Any(a => a.RuleId == r.Id))) reasons.Add("quality_evidence_missing");
        foreach (var assessment in latest)
        {
            ids.Add(assessment.Id);
            if (assessment.RuleVersion != FootballQualityRules.Version) reasons.Add("unsupported_quality_version");
            if (assessment.BlocksEligibility && !(query.Mode == DatasetMode.RetrospectiveReconstruction &&
                assessment.Classification == QualityClassification.IdentityAmbiguous && decision?.Status == BetStats.Domain.Identity.ResolutionStatus.Resolved)) reasons.Add(assessment.ReasonCode);
        }
        // Different sources can disagree without proving either is wrong.
        if (o.CanonicalId is not null)
        {
            var others = batch is null ? await db.Observations.AsNoTracking().Where(n => n.DataSourceId != o.DataSourceId && n.Type == o.Type &&
                n.CanonicalSportingEventId == o.CanonicalSportingEventId && n.CanonicalParticipantId == o.CanonicalParticipantId &&
                n.AvailableAtUtc <= query.AsOfUtc && n.RecordedAtUtc <= query.AsOfUtc &&
                !db.Observations.Any(p => p.ProviderIdentityId == n.ProviderIdentityId && p.Type == n.Type && p.Version > n.Version && p.AvailableAtUtc <= query.AsOfUtc && p.RecordedAtUtc <= query.AsOfUtc))
                .OrderBy(n => n.Id).Take(101).ToListAsync(token) : batch.Others[o.Id].ToList();
            if (others.Count > 100) throw new InvalidOperationException("Cross-source conflict evidence exceeds bound.");
            conflictIds.AddRange(others.Where(n => n.DateValue != o.DateValue || n.StatusValue != o.StatusValue || n.TimestampValueUtc != o.TimestampValueUtc || n.TextValue != o.TextValue).Select(n => n.Id));
            if (conflictIds.Count > 0) reasons.Add("cross_source_observation_conflict");
        }
        Guid? policy = null;
        var purposes = new[] { query.Purpose, DataPurpose.InternalAnalytics, DataPurpose.HistoricalRetention }.Distinct().ToArray();
        var historical = batch is null ? await policies.EvaluateManyAsync(o.DataSourceId, purposes, knowledge, query.Context, token) : batch.Historical[o.DataSourceId];
        var currents = batch is null ? await policies.EvaluateManyAsync(o.DataSourceId, purposes, now, query.Context, token) : batch.Current[o.DataSourceId];
        foreach (var purpose in purposes)
        {
            var evaluation = historical[purpose];
            policy = evaluation.PolicyId;
            if (!evaluation.Allowed) reasons.Add("historical_policy_" + evaluation.Reason);
            var current = currents[purpose];
            if (!current.Allowed) reasons.Add("current_policy_" + current.Reason);
            foreach (var restriction in current.Restrictions.Concat(evaluation.Restrictions))
                if (restriction.MaximumRetentionDays is { } days && raw is not null && now - raw.RetrievedAtUtc > TimeSpan.FromDays(days)) reasons.Add("retention_age_exceeded");
        }
        if ((batch is null ? await sources.ReadAsync(o.DataSourceId, token) : batch.Status[o.DataSourceId]) != SourceOperationalStatus.Enabled) reasons.Add("source_not_enabled");
        return new(reasons.Count == 0, query.Mode, query.AsOfUtc, query.ReconstructionAtUtc, o.Id, o.CanonicalId, decision?.CanonicalId,
            decision?.Id, policy, reasons.ToArray(), ids.Order().ToArray(), conflictIds.Order().ToArray());
    }
}
