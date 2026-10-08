using BetStats.Domain.Football;
using BetStats.Domain.Quality;

namespace BetStats.Application.Football;

public static class FootballResultRules
{
    public const int Version = 1;
    public static IReadOnlyList<string> Catalog { get; } = ["result_goals", "result_halftime", "result_transition", "result_correction"];
    public static IReadOnlyList<QualityIssue> Assess(FootballResultValue value, FootballResultValue? previous = null,
        bool newerEvidence = true, bool sameEvidenceTime = false)
    {
        bool Pair(FootballScore s) => (s.Home is null) == (s.Away is null) && s.Home is not < 0 && s.Away is not < 0 && (long)(s.Home ?? 0) + (s.Away ?? 0) <= int.MaxValue;
        var goals = Pair(value.FullTime) && Pair(value.HalfTime) && Enum.IsDefined(value.Status) && Enum.IsDefined(value.Basis);
        var half = value.Basis != FootballScoreBasis.RegulationTime || value.FullTime.Home is null || value.HalfTime.Home is null ||
            value.HalfTime.Home <= value.FullTime.Home && value.HalfTime.Away <= value.FullTime.Away;
        var transition = previous is null || Transition(previous.Status, value.Status);
        var changed = previous is not null && previous != value;
        return [Issue("result_goals", goals, "result_invalid_goals"), Issue("result_halftime", half, "result_halftime_exceeds_fulltime"),
            Issue("result_transition", transition, "result_invalid_status_transition", QualityClassification.InvalidTransition),
            Issue("result_correction", !changed || newerEvidence && !sameEvidenceTime, sameEvidenceTime ? "result_conflicting_report" : "result_stale_report", QualityClassification.ObservationConflict)];
    }
    private static QualityIssue Issue(string id, bool pass, string reason, QualityClassification classification = QualityClassification.Invalid) =>
        new(new(id, Version), pass, pass ? QualitySeverity.Info : QualitySeverity.Error, !pass, pass ? id + "_passed" : reason,
            pass ? QualityClassification.Accepted : classification);
    public static bool Transition(FootballMatchStatus from, FootballMatchStatus to) => from == to || from switch
    {
        FootballMatchStatus.Scheduled => true,
        FootballMatchStatus.Live => to is FootballMatchStatus.HalfTime or FootballMatchStatus.Finished or FootballMatchStatus.Abandoned or FootballMatchStatus.Postponed or FootballMatchStatus.Cancelled,
        FootballMatchStatus.HalfTime => to is FootballMatchStatus.Live or FootballMatchStatus.Finished or FootballMatchStatus.Postponed or FootballMatchStatus.Abandoned or FootballMatchStatus.Cancelled,
        FootballMatchStatus.Postponed => to is FootballMatchStatus.Scheduled or FootballMatchStatus.Finished or FootballMatchStatus.Cancelled,
        FootballMatchStatus.Abandoned => to is FootballMatchStatus.Cancelled,
        _ => false
    };
    public static bool LabelEligible(FootballResultValue value) => value.Status == FootballMatchStatus.Finished &&
        value.Basis == FootballScoreBasis.RegulationTime && value.FullTime.Home is >= 0 && value.FullTime.Away is >= 0 && Assess(value).All(i => i.Passed);
}

public sealed record FootballOutcomeLabels(int Version, Guid ResultObservationId, DateTime AvailableAtUtc, DateTime RecordedAtUtc,
    string? Winner, int? FullTimeTotalGoals, bool? BothTeamsScored, bool? Over2_5Goals, int? HalfTimeTotalGoals);
public static class FootballOutcomes
{
    public static FootballOutcomeLabels? Derive(FootballResultObservation result)
    {
        var full = FootballResultRules.LabelEligible(result.Value);
        var half = result.Value.Status is FootballMatchStatus.HalfTime or FootballMatchStatus.Finished &&
            result.Value.Basis == FootballScoreBasis.RegulationTime && result.Value.HalfTime.Home is >= 0 && result.Value.HalfTime.Away is >= 0 &&
            FootballResultRules.Assess(result.Value).All(x => x.Passed);
        if (!full && !half) return null;
        var home = result.Value.FullTime.Home; var away = result.Value.FullTime.Away;
        return new(1, result.Id, result.AvailableAtUtc, result.RecordedAtUtc,
            full ? home > away ? "HomeWin" : home < away ? "AwayWin" : "Draw" : null,
            full ? checked(home + away) : null, full ? home > 0 && away > 0 : null, full ? (long?)home + away > 2 : null,
            half ? checked(result.Value.HalfTime.Home + result.Value.HalfTime.Away) : null);
    }
}
