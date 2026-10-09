using BetStats.Domain.Coverage;

namespace BetStats.Application.Coverage;

public static class PredictionTimeBoundary
{
    public const string SourceBoundKickoffV1 = "source-bound-kickoff-v1";
    public static bool Allows(string? policy, Guid eventId, Guid dateObservationId, DateOnly date, DateTime cutoff,
        IReadOnlyList<EventTimeClaimResult> times)
    {
        if (policy is null) return cutoff < date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        if (policy != SourceBoundKickoffV1 || times.Count != 1) return false;
        var time = times[0];
        return time.EventId == eventId && time.Evidence.DateObservationId == dateObservationId && time.IdentityDecisionId != Guid.Empty &&
            time.Evidence.AvailableAtUtc <= cutoff && time.Evidence.RecordedAtUtc <= cutoff &&
            time.Resolution.Precision is EventTimePrecision.Minute or EventTimePrecision.Second &&
            time.Resolution.UtcInstant is { } kickoff && kickoff > cutoff && DateOnly.FromDateTime(kickoff) == date &&
            EventTimeRules.Resolve(time.Evidence.Value).UtcInstant == kickoff;
    }
}
