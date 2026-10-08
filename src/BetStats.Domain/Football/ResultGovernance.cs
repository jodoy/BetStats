using BetStats.Domain.Coverage;
using System.Text.Json.Serialization;

namespace BetStats.Domain.Football;

public enum ResultCoverageStatus { Unknown, Partial, Complete, Empty, Conflict, Expired }
public sealed record ResultCoverageScope(Guid SourceId, Guid CompetitionId, Guid SeasonId, Guid? ParticipantId,
    string CompetitionReference, string SeasonReference, CoverageInterval Interval)
{
    public void Validate()
    {
        if (SourceId == Guid.Empty || CompetitionId == Guid.Empty || SeasonId == Guid.Empty || ParticipantId == Guid.Empty ||
            string.IsNullOrWhiteSpace(CompetitionReference) || CompetitionReference.Length > 50 || string.IsNullOrWhiteSpace(SeasonReference) || SeasonReference.Length > 50)
            throw new ArgumentException("Explicit result scope required.");
        Interval.Validate();
        if (Interval.Kind != IntervalKind.Calendar || Interval.CalendarBasis != "UTC-calendar")
            throw new ArgumentException("Result coverage supports explicit UTC-calendar intervals only.");
    }
    public bool SameDimensions(ResultCoverageScope other) => this with { Interval = other.Interval } == other;
}
public sealed class ResultInventoryEvidence
{
    public Guid Id { get; init; }
    public Guid SourceId { get; init; }
    public required ResultCoverageScope Scope { get; init; }
    public ResultCoverageStatus Claim { get; init; }
    public Guid RawId { get; init; }
    public required string RawHash { get; init; }
    public Guid PolicyId { get; init; }
    public int Version { get; init; } = 1;
    public Guid? CorrectsId { get; init; }
    public DateTime? PublishedAtUtc { get; init; }
    public DateTime RetrievedAtUtc { get; init; }
    public DateTime AvailableAtUtc { get; init; }
    public DateTime ValidUntilUtc { get; init; }
    public required string OperatorId { get; init; }
    public required string Reason { get; init; }
    [JsonInclude] public DateTime RecordedAtUtc { get; private set; }
}
public sealed class ResultInventoryReview
{
    public Guid Id { get; init; }
    public Guid EvidenceId { get; init; }
    public int Sequence { get; init; }
    public bool Approved { get; init; }
    public required string OperatorId { get; init; }
    public required string Reason { get; init; }
    [JsonInclude] public DateTime RecordedAtUtc { get; private set; }
}
public sealed class EventEndEvidence
{
    public Guid Id { get; init; }
    public Guid SourceId { get; init; }
    public Guid ResultObservationId { get; init; }
    public Guid RawId { get; init; }
    public required string RawHash { get; init; }
    public Guid PolicyId { get; init; }
    public Guid IdentityDecisionId { get; init; }
    public required EventTimeValue Value { get; init; }
    public Guid? CorrectsId { get; init; }
    public int Version { get; init; }
    public DateTime? PublishedAtUtc { get; init; }
    public DateTime RetrievedAtUtc { get; init; }
    public DateTime AvailableAtUtc { get; init; }
    public required string OperatorId { get; init; }
    public required string Reason { get; init; }
    [JsonInclude] public DateTime RecordedAtUtc { get; private set; }
}
