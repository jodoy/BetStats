using BetStats.Application.Governance;
using BetStats.Application.Providers;
using BetStats.Application.Quality;
using BetStats.Domain.Governance;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Quality;

public sealed class AnalyticalQualityGate(BetStatsDbContext db, ISourcePolicyEvaluator policies, ISourceOperationalStatus sources) : IAnalyticalQualityGate
{
    public async Task<QualityGateResult> EvaluateAsync(EligibilityQuery query, CancellationToken token = default)
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
        var now = await QualityPersistence.Now(db, token);
        if (knowledge > now) throw new ArgumentException("Future knowledge timestamps are not supported.");
        var reasons = new SortedSet<string>(StringComparer.Ordinal); var ids = new List<Guid>(); var conflictIds = new List<Guid>();
        var o = await db.Observations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == query.ObservationId, token);
        if (o is null) return new(false, query.Mode, query.AsOfUtc, query.ReconstructionAtUtc, query.ObservationId, null, null, null, null, ["observation_missing"], []);
        if (o.AvailableAtUtc > query.AsOfUtc || o.RecordedAtUtc > query.AsOfUtc) reasons.Add("observation_not_known_at_cutoff");
        var decision = await QualityPersistence.Decision(db, o.ProviderIdentityId, knowledge, token);
        if (decision?.Status != BetStats.Domain.Identity.ResolutionStatus.Resolved) reasons.Add("identity_unresolved_at_knowledge_time");
        if (query.Mode == DatasetMode.HistoricalAsKnown && (o.CanonicalId is null || decision?.CanonicalId != o.CanonicalId)) reasons.Add("frozen_target_mismatch");
        var raw = await db.RawPayloads.AsNoTracking().SingleOrDefaultAsync(r => r.Id == o.RawPayloadId, token);
        if (raw is null || raw.DataSourceId != o.DataSourceId || raw.RecordedAtUtc > query.AsOfUtc || raw.RetrievedAtUtc > o.RetrievedAtUtc) reasons.Add("provenance_incomplete_at_cutoff");
        if (await db.Observations.AnyAsync(n => n.ProviderIdentityId == o.ProviderIdentityId && n.Type == o.Type && n.Version > o.Version &&
            n.AvailableAtUtc <= query.AsOfUtc && n.RecordedAtUtc <= query.AsOfUtc, token)) reasons.Add("superseded_observation");
        var anchor = await db.ProviderIdentities.AsNoTracking().SingleAsync(i => i.Id == o.ProviderIdentityId, token);
        var assessments = await db.QualityAssessments.AsNoTracking().Where(a => a.DataSourceId == o.DataSourceId && a.RawPayloadId == o.RawPayloadId &&
            (a.ProviderIdentityId == o.ProviderIdentityId || a.RecordReference == anchor.ExternalId) && a.AssessedAtUtc <= knowledge && a.RecordedAtUtc <= knowledge)
            .OrderByDescending(a => a.RecordedAtUtc).ThenByDescending(a => a.Id).Take(1001).ToListAsync(token);
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
            var others = await db.Observations.AsNoTracking().Where(n => n.DataSourceId != o.DataSourceId && n.Type == o.Type &&
                n.CanonicalSportingEventId == o.CanonicalSportingEventId && n.CanonicalParticipantId == o.CanonicalParticipantId &&
                n.AvailableAtUtc <= query.AsOfUtc && n.RecordedAtUtc <= query.AsOfUtc &&
                !db.Observations.Any(p => p.ProviderIdentityId == n.ProviderIdentityId && p.Type == n.Type && p.Version > n.Version && p.AvailableAtUtc <= query.AsOfUtc && p.RecordedAtUtc <= query.AsOfUtc))
                .OrderBy(n => n.Id).Take(101).ToListAsync(token);
            if (others.Count > 100) throw new InvalidOperationException("Cross-source conflict evidence exceeds bound.");
            conflictIds.AddRange(others.Where(n => n.DateValue != o.DateValue || n.StatusValue != o.StatusValue || n.TimestampValueUtc != o.TimestampValueUtc || n.TextValue != o.TextValue).Select(n => n.Id));
            if (conflictIds.Count > 0) reasons.Add("cross_source_observation_conflict");
        }
        Guid? policy = null;
        foreach (var purpose in new[] { query.Purpose, DataPurpose.InternalAnalytics, DataPurpose.HistoricalRetention }.Distinct())
        {
            var evaluation = await policies.EvaluateAsync(o.DataSourceId, purpose, knowledge, query.Context, token);
            policy = evaluation.PolicyId;
            if (!evaluation.Allowed) reasons.Add("historical_policy_" + evaluation.Reason);
            var current = await policies.EvaluateAsync(o.DataSourceId, purpose, now, query.Context, token);
            if (!current.Allowed) reasons.Add("current_policy_" + current.Reason);
            foreach (var restriction in current.Restrictions.Concat(evaluation.Restrictions))
                if (restriction.MaximumRetentionDays is { } days && raw is not null && now - raw.RetrievedAtUtc > TimeSpan.FromDays(days)) reasons.Add("retention_age_exceeded");
        }
        if (await sources.ReadAsync(o.DataSourceId, token) != SourceOperationalStatus.Enabled) reasons.Add("source_not_enabled");
        return new(reasons.Count == 0, query.Mode, query.AsOfUtc, query.ReconstructionAtUtc, o.Id, o.CanonicalId, decision?.CanonicalId,
            decision?.Id, policy, reasons.ToArray(), ids.Order().ToArray(), conflictIds.Order().ToArray());
    }
}
