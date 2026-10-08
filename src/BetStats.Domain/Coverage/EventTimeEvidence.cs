using System.Text.Json.Serialization;
namespace BetStats.Domain.Coverage;

public enum EventTimePrecision { Unknown, DateOnly, Minute, Second }
public sealed record EventTimeValue(DateOnly? LocalDate, TimeOnly? LocalTime, string? TimeZoneId, int? OffsetMinutes,
    DateTime? SourceUtcInstant, EventTimePrecision Precision);
public sealed class EventTimeEvidence
{
    public Guid Id { get; init; }
    public Guid SourceId { get; init; }
    public Guid ProviderIdentityId { get; init; }
    public Guid DateObservationId { get; init; }
    public Guid RawId { get; init; }
    public required string RawHash { get; init; }
    public Guid PolicyId { get; init; }
    public required EventTimeValue Value { get; init; }
    public required string EvidenceReference { get; init; }
    public Guid? CorrectsId { get; init; }
    public int Version { get; init; }
    public DateTime? SourcePublishedAtUtc { get; init; }
    public DateTime RetrievedAtUtc { get; init; }
    public DateTime AvailableAtUtc { get; init; }
    public required string OperatorId { get; init; }
    public required string Reason { get; init; }
    [JsonInclude] public DateTime RecordedAtUtc { get; private set; }
}
