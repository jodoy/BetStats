using BetStats.Application.Pipeline;

namespace BetStats.UnitTests;

public sealed class PipelineScheduleTests
{
    private static readonly DateTime At = new(2026, 10, 25, 1, 0, 0, DateTimeKind.Utc);
    [Fact] public void Utc_recurrence_skips_missed_occurrences_across_dst_boundary()
    {
        var schedule = new PipelineSchedule(At, 3600); schedule.Validate();
        Assert.Equal(At.AddHours(4), schedule.Next(At, At.AddHours(3)));
        Assert.Equal(At.AddHours(1), schedule.Next(At, At.AddTicks(-10)));
        Assert.Null(new PipelineSchedule(At).Next(At, At.AddDays(1)));
    }
    [Theory] [InlineData("Europe/Warsaw", "NotApplicable")] [InlineData("UTC", "GuessEarlier")]
    public void Local_wall_clocks_and_implicit_dst_are_rejected(string zone, string dst) =>
        Assert.Throws<ArgumentException>(() => new PipelineSchedule(At, 60, zone, dst).Validate());
    [Fact] public void Definition_and_audit_approval_are_bounded()
    {
        var definition = new PipelineDefinition(1, PipelineKind.LocalSynchronization, new(At), "{}"); definition.Validate();
        Assert.Equal(43200, definition.PredictionHorizonSeconds);
        Assert.Equal(64, definition.Fingerprint.Length);
        Assert.Equal(definition.Fingerprint, BetStats.Application.Datasets.CanonicalDatasetJson.Fingerprint(definition));
        var execution = Guid.NewGuid();
        Assert.Equal(PipelineOperationIds.Child(execution, "predictions"), PipelineOperationIds.Child(execution, "predictions"));
        Assert.NotEqual(PipelineOperationIds.Child(execution, "predictions"), PipelineOperationIds.Child(execution, "local-import"));
        Assert.Throws<ArgumentException>(() => (definition with { MaximumAttempts = 6 }).Validate());
        Assert.Throws<ArgumentException>(() => (definition with { LeaseSeconds = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => new PipelineApproval("operator", "reason", false).Validate());
        Assert.Throws<ArgumentException>(() => new PipelineSchedule(DateTime.SpecifyKind(At, DateTimeKind.Unspecified)).Validate());
    }
}
