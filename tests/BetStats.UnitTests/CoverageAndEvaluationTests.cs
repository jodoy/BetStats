using System.Text;
using BetStats.Application.Coverage;
using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Domain.Coverage;
using BetStats.Domain.Governance;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;

namespace BetStats.UnitTests;

public sealed class CoverageAndEvaluationTests
{
    private static readonly DateTime T = new(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly CoverageScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "football-match",
        ObservationType.EventDate, "fiction", "2026", Interval(1, 11));
    private static CoverageInterval Interval(int a, int b) => new(IntervalKind.Calendar, null, null, new(2026, 1, a), new(2026, 1, b), "UTC-calendar");
    private static CoverageItem Item(CoverageStatus status, CoverageInterval? interval = null, Guid[]? ids = null) => new(new() {
        Id = Guid.NewGuid(), SourceId = Scope.SourceId, Scope = Scope with { Interval = interval ?? Scope.Interval }, Claim = status, Basis = CoverageBasis.OwnedFixtureInventory,
        EvidenceReference = "fictional-inventory", RawId = Guid.NewGuid(), RawHash = new('a', 64), PolicyId = Guid.NewGuid(), Version = 1, SupportingObservationIds = ids ?? [],
        RetrievedAtUtc = T, AvailableAtUtc = T, ValidUntilUtc = T.AddDays(1), OperatorId = "fictional", Reason = "unit proof" }, null, status, [], [], []);
    private static CoverageReport Report(CoverageStatus status, bool authorized = true) => new(new(Scope with { Interval = Interval(10, 20) }, T, DatasetMode.HistoricalAsKnown, null, DataPurpose.InternalAnalytics, new()),
        status, authorized, [Item(status)], [], [], [], [], []);
    private static FeatureCoverageRequirement Requirement(bool partial) => new("observed", 1, [ObservationType.EventDate], 10, true, "Completed", 1, partial, !partial);

    [Theory]
    [InlineData(CoverageStatus.Unknown)] [InlineData(CoverageStatus.Partial)] [InlineData(CoverageStatus.VerifiedComplete)]
    [InlineData(CoverageStatus.VerifiedEmpty)] [InlineData(CoverageStatus.Conflicting)] [InlineData(CoverageStatus.Expired)]
    public void Classification_is_explicit_and_empty_is_not_unknown(CoverageStatus status) => Assert.Equal(status, CoverageRules.Classify(Scope.Interval, [Item(status)]));

    [Fact] public void Ten_observation_ids_without_a_guarantee_never_imply_completeness() =>
        Assert.Equal(CoverageStatus.Unknown, CoverageRules.Classify(Scope.Interval, [Item(CoverageStatus.Unknown, ids: Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray())]));
    [Fact] public void Adjacent_half_open_intervals_have_no_overlap_and_join_without_a_gap()
    {
        Assert.Null(CoverageRules.Intersection(Interval(1, 5), Interval(5, 11)));
        Assert.Empty(CoverageRules.Gaps(Scope.Interval, [Interval(5, 11), Interval(1, 5)]));
    }
    [Fact] public void Intersections_are_clipped_and_gaps_are_stable() {
        Assert.Equal(Interval(3, 5), CoverageRules.Intersection(Interval(1, 5), Interval(3, 9)));
        Assert.Equal(new[] { Interval(1, 3), Interval(5, 8), Interval(10, 11) }, CoverageRules.Gaps(Scope.Interval, [Interval(8, 10), Interval(3, 5)]));
    }
    [Fact] public void Incompatible_calendar_and_utc_intervals_are_rejected() => Assert.Throws<ArgumentException>(() =>
        CoverageRules.Intersection(Scope.Interval, new(IntervalKind.Utc, T, T.AddDays(1), null, null, null)));
    [Theory] [InlineData(5, 5)] [InlineData(10, 1)] public void Empty_or_inverted_intervals_are_rejected(int a, int b) => Assert.Throws<ArgumentException>(() => Interval(a, b).Validate());
    [Fact] public void Oversized_and_invalid_scope_are_rejected() {
        Assert.Throws<ArgumentException>(() => (Scope.Interval with { EndDate = new(2029, 1, 1) }).Validate());
        Assert.Throws<ArgumentException>(() => (Scope with { SourceId = Guid.Empty }).Validate());
        Assert.Throws<ArgumentException>(() => (Scope with { ObservationType = (ObservationType)999 }).Validate());
    }
    [Fact] public void Contradictory_complete_and_empty_claims_fail_closed() => Assert.Equal(CoverageStatus.Conflicting,
        CoverageRules.Classify(Scope.Interval, [Item(CoverageStatus.VerifiedComplete), Item(CoverageStatus.VerifiedEmpty)]));
    [Fact] public void A_strong_subset_does_not_establish_whole_scope_completeness() => Assert.Equal(CoverageStatus.Partial,
        CoverageRules.Classify(Scope.Interval, [Item(CoverageStatus.VerifiedComplete, Interval(3, 5))]));
    [Theory]
    [InlineData(CoverageStatus.Partial, false, FeatureCoverageOutcome.InsufficientCoverage)]
    [InlineData(CoverageStatus.Unknown, false, FeatureCoverageOutcome.UnknownCoverage)]
    [InlineData(CoverageStatus.Partial, true, FeatureCoverageOutcome.EligibleWithPartialCoverage)]
    [InlineData(CoverageStatus.Unknown, true, FeatureCoverageOutcome.EligibleWithPartialCoverage)]
    [InlineData(CoverageStatus.VerifiedComplete, false, FeatureCoverageOutcome.Eligible)]
    [InlineData(CoverageStatus.VerifiedEmpty, false, FeatureCoverageOutcome.Eligible)]
    [InlineData(CoverageStatus.Expired, true, FeatureCoverageOutcome.ExpiredCoverage)]
    [InlineData(CoverageStatus.Conflicting, true, FeatureCoverageOutcome.ConflictingCoverage)]
    public void Feature_coverage_never_upgrades_partial_history(CoverageStatus status, bool partial, FeatureCoverageOutcome expected) =>
        Assert.Equal(expected, CoverageRules.Gate(Requirement(partial), [Report(status)]).Outcome);
    [Fact] public void Permission_denial_overrides_verified_coverage() => Assert.Equal(FeatureCoverageOutcome.Unauthorized, CoverageRules.Gate(Requirement(true), [Report(CoverageStatus.VerifiedComplete, false)]).Outcome);
    [Fact] public void Feature_scope_mismatch_is_not_accepted() => Assert.Throws<ArgumentException>(() => CoverageRules.Gate(Requirement(true) with { LookbackDays = 30 }, [Report(CoverageStatus.VerifiedComplete)]));
    [Fact] public void All_existing_features_declare_partial_evidence_requirements() => Assert.All(FootballMetadataFeatures.Catalog, f => { var r = FootballMetadataFeatures.Requirement(f); Assert.True(r.PartialAllowed); Assert.False(r.CompletenessRequired); Assert.Equal(2, r.ObservationTypes.Count); });
    [Fact] public void Date_only_never_provides_utc_kickoff() => Assert.Null(EventTimeRules.Resolve(new(new(2026, 1, 1), null, null, null, null, EventTimePrecision.DateOnly)).UtcInstant);
    [Fact] public void A_local_time_without_context_does_not_become_utc() => Assert.Equal("timezone_context_missing", EventTimeRules.Resolve(new(new(2026, 1, 1), new(12, 0), null, null, null, EventTimePrecision.Minute)).Reason);
    [Theory] [InlineData(10, 25, "dst_ambiguous")] [InlineData(3, 29, "dst_gap")]
    public void Warsaw_dst_does_not_choose_or_invent_an_instant(int month, int day, string reason) => Assert.Equal(reason,
        EventTimeRules.Resolve(new(new(2026, month, day), new(2, 30), "Europe/Warsaw", null, null, EventTimePrecision.Minute)).Reason);
    [Fact] public void Explicit_offset_disambiguates_dst_only_when_compatible() {
        var v = new EventTimeValue(new(2026, 10, 25), new(2, 30), "Europe/Warsaw", 120, null, EventTimePrecision.Minute);
        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), EventTimeRules.Resolve(v).UtcInstant);
        Assert.Equal("offset_timezone_conflict", EventTimeRules.Resolve(v with { OffsetMinutes = 180 }).Reason);
    }
    [Fact] public void Contradictory_source_utc_is_an_uncertainty() => Assert.Equal("source_utc_conflict", EventTimeRules.Resolve(new(new(2026, 1, 20), new(12, 0), null, 0, T, EventTimePrecision.Minute)).Reason);
    [Fact] public void Offset_overflow_cannot_wrap_date_range() => Assert.Equal("utc_overflow", EventTimeRules.Resolve(new(DateOnly.MinValue, TimeOnly.MinValue, null, 840, null, EventTimePrecision.Minute)).Reason);
    [Fact] public void Invalid_precision_offset_and_date_only_time_are_rejected() {
        Assert.Throws<ArgumentException>(() => EventTimeRules.Resolve(new(new(2026, 1, 1), new(12, 0, 1), null, 0, null, EventTimePrecision.Minute)));
        Assert.Throws<ArgumentException>(() => EventTimeRules.Resolve(new(new(2026, 1, 1), null, null, 841, null, EventTimePrecision.DateOnly)));
    }
    [Fact] public void Independent_rescheduling_claims_are_conflicting() => Assert.True(EventTimeRules.Conflicts(
        new(new(2026, 1, 1), new(12, 0), null, 0, null, EventTimePrecision.Minute), new(new(2026, 1, 2), new(12, 0), null, 0, null, EventTimePrecision.Minute)));
    private static EvaluationDefinition Definition(EvaluationTarget target = EvaluationTarget.BothTeamsScoring) => new(1, Scope.SportId, target, DatasetMode.HistoricalAsKnown, null,
        PredictionCutoffPolicy.BeforeCalendarDay, TimeSpan.FromDays(1), ["features", "event-time", "quality", "coverage", "source-policy"], target.ToString(), 1,
        Requirement(false), [EvaluationContracts.Metrics.Single(m => m.Name == (target == EvaluationTarget.TotalGoals ? "mae" : "brier"))]);
    private static EvaluationEventEvidence CalendarEvidence() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new(new(2026, 1, 21), null, null, null, null, EventTimePrecision.DateOnly), true, T, T, "UTC-calendar");
    private static readonly FeatureCoverageDecision Complete = new("observed", FeatureCoverageOutcome.Eligible, [], [], []);
    [Theory] [InlineData(EvaluationTarget.MatchWinner)] [InlineData(EvaluationTarget.TotalGoals)] [InlineData(EvaluationTarget.BothTeamsScoring)] [InlineData(EvaluationTarget.FirstHalfGoalOccurrence)]
    public void Future_target_definitions_are_valid_without_computing_outcomes(EvaluationTarget target) => Definition(target).Validate();
    [Fact] public void Late_label_is_not_a_feature_but_is_valid_for_later_evaluation() {
        var label = new OutcomeAvailability(Guid.NewGuid(), T.AddHours(3), T.AddHours(4), 1);
        Assert.False(EvaluationContracts.CanUseAsFeature(label, T));
        Assert.True(EvaluationContracts.Eligibility(Definition(), T, T.AddHours(-1), T, label, T.AddDays(1), true, Complete, CalendarEvidence()).Eligible);
        Assert.Contains("feature_first_available_after_prediction", EvaluationContracts.Eligibility(Definition(), T, T.AddHours(1), T, label, T.AddDays(1), true, Complete, CalendarEvidence()).Reasons);
        Assert.Contains("feature_first_recorded_after_prediction", EvaluationContracts.Eligibility(Definition(), T, T.AddHours(-1), T.AddHours(1), label, T.AddDays(1), true, Complete, CalendarEvidence()).Reasons);
    }
    [Fact] public void Labels_need_trusted_receipt_permission_coverage_and_version() {
        var label = new OutcomeAvailability(Guid.NewGuid(), T.AddHours(1), T.AddDays(2), 2);
        var denied = EvaluationContracts.Eligibility(Definition(), T, T, T, label, T.AddDays(1), false, Complete with { Outcome = FeatureCoverageOutcome.InsufficientCoverage }, CalendarEvidence());
        Assert.False(denied.Eligible); Assert.Equal(4, denied.Reasons.Count);
    }
    [Fact] public void Invalid_metric_formats_and_undefined_semantics_are_rejected() {
        Assert.Throws<ArgumentException>(() => (Definition() with { Metrics = [EvaluationContracts.Metrics.Single(m => m.Name == "mae")] }).Validate());
        Assert.Throws<ArgumentException>(() => (Definition() with { Metrics = [EvaluationContracts.Metrics[0] with { MinimumSamples = 0 }] }).Validate());
        Assert.Throws<ArgumentException>(() => (Definition(EvaluationTarget.MatchWinner) with { Metrics = [EvaluationContracts.Metrics.Single(m => m.Name == "calibration_error")] }).Validate());
        Assert.All(EvaluationContracts.Metrics, m => { Assert.Equal(1, m.Version); Assert.Equal(100, m.MinimumSamples); Assert.Equal("exclude-and-report", m.MissingLabelBehavior); });
    }
    [Fact] public void V1_optional_extensions_do_not_change_original_canonical_bytes() {
        var legacy = Encoding.UTF8.GetBytes("{\"Definition\":null,\"DefinitionFingerprint\":\"fixture\",\"ManifestVersion\":1,\"QualityRuleVersions\":[],\"Rows\":[],\"SerializerVersion\":1}");
        var manifest = CanonicalDatasetJson.Deserialize<DatasetManifest>(legacy);
        Assert.Equal(legacy, CanonicalDatasetJson.Serialize(manifest)); Assert.Null(manifest.Governance);
    }
}
