using BetStats.Application.Football;
using BetStats.Domain.Football;

namespace BetStats.UnitTests;

public sealed class FootballResultTests
{
    private static FootballResultValue Value(FootballMatchStatus status, int? h, int? a, int? hh = null, int? ha = null, FootballScoreBasis basis = FootballScoreBasis.RegulationTime) => new(status, basis, new(h, a), new(hh, ha));
    [Theory]
    [InlineData(2, 1, "HomeWin", 3, true, true)]
    [InlineData(0, 0, "Draw", 0, false, false)]
    [InlineData(0, 2, "AwayWin", 2, false, false)]
    public void Justified_regulation_scores_produce_exact_labels(int h, int a, string winner, int total, bool both, bool over)
    {
        var label = FootballOutcomes.Derive(new() { Id = Guid.NewGuid(), Value = Value(FootballMatchStatus.Finished, h, a) });
        Assert.NotNull(label); Assert.Equal(winner, label.Winner); Assert.Equal(total, label.FullTimeTotalGoals);
        Assert.Equal(both, label.BothTeamsScored); Assert.Equal(over, label.Over2_5Goals); Assert.Null(label.HalfTimeTotalGoals);
    }
    [Theory]
    [InlineData(FootballMatchStatus.Scheduled)]
    [InlineData(FootballMatchStatus.Live)]
    [InlineData(FootballMatchStatus.HalfTime)]
    [InlineData(FootballMatchStatus.Postponed)]
    [InlineData(FootballMatchStatus.Cancelled)]
    [InlineData(FootballMatchStatus.Abandoned)]
    public void Non_finished_status_never_produces_outcomes(FootballMatchStatus status) => Assert.False(FootballResultRules.LabelEligible(Value(status, 2, 1)));
    [Theory]
    [InlineData(FootballScoreBasis.Unknown)]
    [InlineData(FootballScoreBasis.IncludesExtraTime)]
    [InlineData(FootballScoreBasis.PenaltyShootout)]
    public void Unsupported_score_bases_cannot_supply_regulation_labels(FootballScoreBasis basis) => Assert.False(FootballResultRules.LabelEligible(Value(FootballMatchStatus.Finished, 2, 1, basis: basis)));
    [Theory]
    [InlineData(-1, 0, null, null)]
    [InlineData(1, null, null, null)]
    [InlineData(2, 1, 3, 0)]
    [InlineData(2, 1, 1, 2)]
    [InlineData(2, 1, 1, null)]
    [InlineData(int.MaxValue, 1, null, null)]
    public void Invalid_scores_fail_deterministic_quality(int? h, int? a, int? hh, int? ha) => Assert.Contains(FootballResultRules.Assess(Value(FootballMatchStatus.Finished, h, a, hh, ha)), x => x.BlocksEligibility);
    [Fact]
    public void Missing_scores_are_not_zero_and_supported_half_time_is_preserved()
    {
        Assert.False(FootballResultRules.LabelEligible(Value(FootballMatchStatus.Finished, null, null)));
        Assert.Equal(1, FootballOutcomes.Derive(new() { Value = Value(FootballMatchStatus.Finished, 2, 1, 1, 0) })!.HalfTimeTotalGoals);
    }
    [Fact]
    public void Confirmed_half_time_produces_only_a_half_time_label()
    {
        var value = Value(FootballMatchStatus.HalfTime, null, null, 1, 0);
        var label = FootballOutcomes.Derive(new() { Value = value });
        Assert.NotNull(label); Assert.Equal(1, label.HalfTimeTotalGoals); Assert.Null(label.Winner);
        Assert.Null(label.FullTimeTotalGoals); Assert.Null(label.BothTeamsScored); Assert.Null(label.Over2_5Goals);
        Assert.False(FootballResultRules.LabelEligible(value));
    }
    [Fact]
    public void Corrections_require_later_evidence_and_terminal_status_cannot_revert()
    {
        var first = Value(FootballMatchStatus.Finished, 2, 1); var corrected = Value(FootballMatchStatus.Finished, 2, 2);
        Assert.All(FootballResultRules.Assess(corrected, first), x => Assert.True(x.Passed));
        Assert.Contains(FootballResultRules.Assess(corrected, first, sameEvidenceTime: true), x => x.ReasonCode == "result_conflicting_report");
        Assert.Contains(FootballResultRules.Assess(corrected, first, newerEvidence: false), x => x.ReasonCode == "result_stale_report");
        Assert.Contains(FootballResultRules.Assess(Value(FootballMatchStatus.Scheduled, null, null), first), x => x.ReasonCode == "result_invalid_status_transition");
    }
    [Theory]
    [InlineData(FootballMatchStatus.Postponed, FootballMatchStatus.Live, false)]
    [InlineData(FootballMatchStatus.Abandoned, FootballMatchStatus.Scheduled, false)]
    [InlineData(FootballMatchStatus.HalfTime, FootballMatchStatus.Postponed, true)]
    [InlineData(FootballMatchStatus.Abandoned, FootballMatchStatus.Cancelled, true)]
    public void Rich_status_transitions_preserve_existing_metadata_publication_semantics(FootballMatchStatus from, FootballMatchStatus to, bool allowed)
        => Assert.Equal(allowed, FootballResultRules.Transition(from, to));
}
