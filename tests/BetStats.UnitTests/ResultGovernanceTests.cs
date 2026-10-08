using BetStats.Application.Coverage;
using BetStats.Application.Evaluation;
using BetStats.Application.Football;
using BetStats.Domain.Coverage;
using BetStats.Domain.Football;
using BetStats.Domain.Quality;

namespace BetStats.UnitTests;

public sealed class ResultGovernanceTests
{
    private static readonly DateTime T = new(2031, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static ResultCoverageScope Scope(int start = 1, int end = 5) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "FICT", "fiction",
        new(IntervalKind.Calendar, null, null, new(2031, 1, start), new(2031, 1, end), "UTC-calendar"));
    private static ResultCoverageItem Item(ResultCoverageScope scope, ResultCoverageStatus status) => new(new() { Id = Guid.NewGuid(), SourceId = scope.SourceId, Scope = scope,
        Claim = status, RawId = Guid.NewGuid(), RawHash = new('a', 64), PolicyId = Guid.NewGuid(), OperatorId = "fixture", Reason = "Unit classification" }, null, status, null, []);
    [Fact] public void Missing_inventory_is_unknown_even_if_metadata_is_complete() => Assert.Equal(ResultCoverageStatus.Unknown, ResultCoverageRules.Classify(Scope(), []));
    [Theory]
    [InlineData(ResultCoverageStatus.Unknown)] [InlineData(ResultCoverageStatus.Partial)] [InlineData(ResultCoverageStatus.Complete)]
    [InlineData(ResultCoverageStatus.Empty)] [InlineData(ResultCoverageStatus.Conflict)] [InlineData(ResultCoverageStatus.Expired)]
    public void Six_distinct_outcomes_are_preserved(ResultCoverageStatus status)
    { var scope = Scope(); Assert.Equal(status, ResultCoverageRules.Classify(scope, [Item(scope, status)])); }
    [Fact] public void Gaps_in_strong_intervals_remain_partial()
    { var scope = Scope(); Assert.Equal(ResultCoverageStatus.Partial, ResultCoverageRules.Classify(scope, [Item(scope with { Interval = scope.Interval with { EndDate = new(2031, 1, 3) } }, ResultCoverageStatus.Complete)])); }
    [Fact] public void Overlapping_complete_and_empty_is_conflict()
    { var scope = Scope(); Assert.Equal(ResultCoverageStatus.Conflict, ResultCoverageRules.Classify(scope, [Item(scope, ResultCoverageStatus.Complete), Item(scope, ResultCoverageStatus.Empty)])); }
    [Fact] public void Observed_conflict_blocks_even_without_inventory() => Assert.Equal(ResultCoverageStatus.Conflict, ResultCoverageRules.Classify(Scope(), [], true));
    [Fact] public void Adjacent_empty_intervals_can_cover_a_bounded_query()
    {
        var scope = Scope(); Assert.Equal(ResultCoverageStatus.Empty, ResultCoverageRules.Classify(scope,
            [Item(scope with { Interval = scope.Interval with { EndDate = new(2031, 1, 3) } }, ResultCoverageStatus.Empty),
             Item(scope with { Interval = scope.Interval with { StartDate = new(2031, 1, 3) } }, ResultCoverageStatus.Empty)]));
    }
    [Fact] public void Unsupported_time_basis_does_not_establish_result_coverage() => Assert.Throws<ArgumentException>(() => (Scope() with { Interval = Scope().Interval with { CalendarBasis = "unknown" } }).Validate());
    [Theory] [InlineData("missing")] [InlineData("unknown")] [InlineData("late")] [InlineData("wrong-result")] [InlineData("before-event")] [InlineData("partial")] [InlineData("valid")]
    public void Evaluation_v3_requires_independent_precise_end_and_result_coverage(string failure)
    {
        var target = Guid.NewGuid(); var result = Guid.NewGuid();
        var definition = new EvaluationDefinition(3, Guid.NewGuid(), EvaluationTarget.BothTeamsScoring, DatasetMode.HistoricalAsKnown, null,
            PredictionCutoffPolicy.BeforeCalendarDay, TimeSpan.FromHours(1), ["features", "event-time", "quality", "coverage", "source-policy"],
            nameof(EvaluationTarget.BothTeamsScoring), 1, new("result", 1, [BetStats.Domain.Observations.ObservationType.EventDate], 30, false, "Completed", 1, false, true), [EvaluationContracts.Metrics.Single(m => m.Name == "brier")]);
        var begin = new EvaluationEventEvidence(target, Guid.NewGuid(), Guid.NewGuid(), new(new(2031, 1, 3), null, null, null, null, EventTimePrecision.DateOnly), true, T, T, "UTC-calendar");
        var value = new EventTimeValue(new(2031, 1, 3), new(2, 0), null, 0, null, EventTimePrecision.Minute);
        EvaluationEndEvidence? end = new(target, result, Guid.NewGuid(), Guid.NewGuid(), value, true, true, T.AddDays(1).AddHours(3), T.AddDays(1).AddHours(3), 1);
        var complete = ResultCoverageStatus.Complete;
        switch (failure)
        {
            case "missing": end = null; break;
            case "unknown": end = end with { Value = new(null, null, null, null, null, EventTimePrecision.Unknown) }; break;
            case "late": end = end with { RecordedUtc = T.AddDays(3) }; break;
            case "wrong-result": end = end with { ResultObservationId = Guid.NewGuid() }; break;
            case "before-event": end = end with { Value = value with { LocalDate = new(2031, 1, 1) } }; break;
            case "partial": complete = ResultCoverageStatus.Partial; break;
        }
        var eligibility = EvaluationContracts.Eligibility(definition, T, T, T, new(result, T.AddDays(1).AddHours(4), T.AddDays(1).AddHours(4), 1),
            T.AddDays(2), true, new("result", FeatureCoverageOutcome.Eligible, [], [], []), begin, end, complete);
        Assert.Equal(failure == "valid", eligibility.Eligible);
    }
}
