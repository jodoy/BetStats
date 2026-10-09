using BetStats.Application.Coverage;

namespace BetStats.UnitTests;

public sealed class PredictionTimePolicyTests
{
    [Fact] public void Default_calendar_policy_is_unchanged_and_precise_policy_never_guesses_missing_evidence()
    {
        var day = new DateOnly(2026, 10, 9); var cutoff = day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
        Assert.False(PredictionTimeBoundary.Allows(null, Guid.NewGuid(), Guid.NewGuid(), day, cutoff, []));
        Assert.True(PredictionTimeBoundary.Allows(null, Guid.NewGuid(), Guid.NewGuid(), day, cutoff.AddDays(-1), []));
        Assert.False(PredictionTimeBoundary.Allows(PredictionTimeBoundary.SourceBoundKickoffV1, Guid.NewGuid(), Guid.NewGuid(), day, cutoff, []));
        Assert.False(PredictionTimeBoundary.Allows("guess-kickoff", Guid.NewGuid(), Guid.NewGuid(), day, cutoff.AddDays(-1), []));
    }
}
