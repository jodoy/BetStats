using BetStats.Application.Coverage;
using BetStats.Application.Evaluation;
using BetStats.Domain.Coverage;
using BetStats.Domain.Governance;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;

namespace BetStats.UnitTests;

public sealed class TemporalIntegrityRegressionTests
{
    private static readonly DateTime T = new(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
    private static FeatureCoverageRequirement Requirement => new("history", 1, [ObservationType.EventDate], 5, false, "Completed", 1, false, true);
    private static CoverageReport Report(DateOnly start, DateOnly end, string basis = "UTC-calendar") => new(new(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
        "football-match", ObservationType.EventDate, "fiction", "2026", new(IntervalKind.Calendar, null, null, start, end, basis)), T,
        DatasetMode.HistoricalAsKnown, null, DataPurpose.InternalAnalytics, new()), CoverageStatus.VerifiedComplete, true, [], [], [], [], [], []);
    [Fact]
    public void Exact_lookback_uses_utc_calendar_day_even_at_midday() => Assert.Equal(FeatureCoverageOutcome.Eligible,
        CoverageRules.Gate(Requirement, [Report(new(2026, 1, 5), new(2026, 1, 10))]).Outcome);
    [Theory]
    [InlineData(4, 9)]
    [InlineData(6, 11)]
    [InlineData(15, 20)]
    public void Equal_length_shifted_or_future_window_is_rejected(int start, int end) => Assert.Throws<ArgumentException>(() =>
        CoverageRules.Gate(Requirement, [Report(new(2026, 1, start), new(2026, 1, end))]));
    [Fact]
    public void Unknown_calendar_basis_cannot_establish_lookback() => Assert.Throws<ArgumentException>(() =>
        CoverageRules.Gate(Requirement, [Report(new(2026, 1, 5), new(2026, 1, 10), "unknown")]));

    private static EvaluationDefinition Definition(PredictionCutoffPolicy policy) => new(2, Guid.NewGuid(), EvaluationTarget.BothTeamsScoring, DatasetMode.HistoricalAsKnown, null,
        policy, TimeSpan.FromHours(2), ["features", "event-time", "quality", "coverage", "source-policy"], "BothTeamsScoring", 1, Requirement, [EvaluationContracts.Metrics[1]]);
    private static EvaluationEventEvidence Evidence(EventTimeValue value, string? basis = null) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), value, true, T, T, basis);
    private static EvaluationEligibility Evaluate(EvaluationEventEvidence? evidence, PredictionCutoffPolicy policy = PredictionCutoffPolicy.BeforeJustifiedKickoff) =>
        EvaluationContracts.Eligibility(Definition(policy), T, T, T, new(Guid.NewGuid(), T.AddDays(1), T.AddDays(1), 1), T.AddDays(2), true,
            new("history", FeatureCoverageOutcome.Eligible, [], [], []), evidence);
    [Theory]
    [InlineData(14, true)]
    [InlineData(15, true)]
    [InlineData(13, false)]
    [InlineData(12, false)]
    public void Kickoff_horizon_is_minimum_lead_time(int hour, bool eligible) => Assert.Equal(eligible,
        Evaluate(Evidence(new(new(2026, 1, 10), new(hour, 0), "UTC", null, null, EventTimePrecision.Minute))).Eligible);
    [Fact]
    public void Missing_source_binding_is_ineligible() => Assert.Contains("justified_event_evidence_missing",
        Evaluate(Evidence(new(new(2026, 1, 10), new(14, 0), "UTC", null, null, EventTimePrecision.Minute)) with { SourceBound = false }).Reasons);
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Date_only_or_missing_timezone_is_not_precise_kickoff(bool dateOnly) => Assert.Contains("precise_justified_kickoff_required",
        Evaluate(Evidence(dateOnly ? new(new(2026, 1, 11), null, null, null, null, EventTimePrecision.DateOnly) : new(new(2026, 1, 10), new(14, 0), null, null, null, EventTimePrecision.Minute))).Reasons);
    [Fact]
    public void Event_time_first_known_after_prediction_is_ineligible() => Assert.Contains("event_evidence_not_known_at_prediction",
        Evaluate(Evidence(new(new(2026, 1, 10), new(14, 0), "UTC", null, null, EventTimePrecision.Minute)) with { RecordedUtc = T.AddMinutes(1) }).Reasons);
    [Fact]
    public void Calendar_policy_requires_explicit_basis_and_never_guesses_kickoff()
    {
        var date = Evidence(new(new(2026, 1, 11), null, null, null, null, EventTimePrecision.DateOnly), "UTC-calendar");
        Assert.True(Evaluate(date, PredictionCutoffPolicy.BeforeCalendarDay).Eligible);
        Assert.Contains("explicit_utc_calendar_day_required", Evaluate(date with { CalendarBasis = null }, PredictionCutoffPolicy.BeforeCalendarDay).Reasons);
        Assert.False(Evaluate(date).Eligible);
    }
}
