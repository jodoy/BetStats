using BetStats.Application.Datasets;

namespace BetStats.Application.Pipeline;

public enum PipelineState { Disabled, Pending, Running, Completed, Failed, Cancelled, Blocked }
public enum PipelineKind { LocalSynchronization, PrematchPrediction, PostmatchEvaluation }
public sealed record PipelineSchedule(DateTime FirstDueUtc, int? IntervalSeconds = null, string TimeZone = "UTC", string DaylightSavingPolicy = "NotApplicable")
{
    public void Validate()
    {
        if (FirstDueUtc.Kind != DateTimeKind.Utc || FirstDueUtc.Ticks % 10 != 0 || TimeZone != "UTC" || DaylightSavingPolicy != "NotApplicable" ||
            IntervalSeconds is < 60 or > 2592000) throw new ArgumentException("Explicit UTC microsecond schedule and bounded interval required.");
    }
    public DateTime? Next(DateTime planned, DateTime now) => IntervalSeconds is { } seconds
        ? planned.AddSeconds((Math.Max(0, (long)Math.Floor((now - planned).TotalSeconds / seconds)) + 1) * seconds) : null;
}
public sealed record PipelineDefinition(int Version, PipelineKind Kind, PipelineSchedule Schedule, string PayloadJson,
    int MaximumAttempts = 3, int LeaseSeconds = 300, int PredictionHorizonSeconds = 43200)
{
    public void Validate()
    {
        if (Schedule is null) throw new ArgumentException("Explicit schedule required.");
        Schedule.Validate();
        if (Version != 1 || !Enum.IsDefined(Kind) || string.IsNullOrWhiteSpace(PayloadJson) || PayloadJson.Length > 1048576 ||
            MaximumAttempts is < 1 or > 5 || LeaseSeconds is < 1 or > 1800 || PredictionHorizonSeconds is < 60 or > 604800)
            throw new ArgumentException("Supported bounded pipeline definition required.");
        try { using var payload = System.Text.Json.JsonDocument.Parse(PayloadJson); if (payload.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new ArgumentException("Object payload required."); }
        catch (System.Text.Json.JsonException) { throw new ArgumentException("Valid object payload required."); }
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Fingerprint => CanonicalDatasetJson.Fingerprint(this);
}
public sealed record PipelineApproval(string Actor, string Reason, bool Approved)
{
    public void Validate()
    {
        if (!Approved || string.IsNullOrWhiteSpace(Actor) || Actor.Length > 200 || string.IsNullOrWhiteSpace(Reason) || Reason.Length > 1000)
            throw new ArgumentException("Explicit actor, reason and approval required; identifiers are audit claims.");
    }
}
public sealed record PipelineClaim(Guid ExecutionId, Guid JobId, int DefinitionVersion, Guid Owner, DateTime PlannedUtc, DateTime StartedUtc,
    DateTime LeaseUntilUtc, int Attempt, PipelineDefinition Definition);
public sealed record PipelineOutcome(PipelineState State, string Category, Guid? ArtifactId = null, string? ArtifactHash = null);
public static class PipelineOperationIds
{
    public static Guid Child(Guid execution, string stage) => new(Convert.FromHexString(CanonicalDatasetJson.Fingerprint(new { execution, stage }))[..16]);
}
