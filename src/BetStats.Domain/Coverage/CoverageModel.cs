using BetStats.Domain.Observations;
using System.Text.Json.Serialization;

namespace BetStats.Domain.Coverage;

public enum CoverageStatus { Unknown, Partial, VerifiedComplete, VerifiedEmpty, Conflicting, Expired }
public enum IntervalKind { Utc, Calendar }
public enum CoverageBasis { ManualStatement, OwnedFixtureInventory }
public enum CoverageReviewStatus { Approved, Rejected }
public sealed record CoverageInterval(IntervalKind Kind, DateTime? StartUtc, DateTime? EndUtc, DateOnly? StartDate, DateOnly? EndDate, string? CalendarBasis)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind)) throw new ArgumentException("Unknown interval kind.");
        if (Kind == IntervalKind.Utc)
        {
            if (StartUtc is not { Kind: DateTimeKind.Utc } start || EndUtc is not { Kind: DateTimeKind.Utc } end || start.Ticks % 10 != 0 || end.Ticks % 10 != 0 ||
                end <= start || end - start > TimeSpan.FromDays(731) || StartDate is not null || EndDate is not null || CalendarBasis is not null)
                throw new ArgumentException("Bounded UTC half-open interval required.");
        }
        else if (StartDate is not { } a || EndDate is not { } b || b <= a || b.DayNumber - a.DayNumber > 731 || StartUtc is not null || EndUtc is not null ||
            string.IsNullOrWhiteSpace(CalendarBasis) || CalendarBasis.Length > 100) throw new ArgumentException("Explicit bounded calendar interval required.");
    }
    public long Start => Kind == IntervalKind.Utc ? StartUtc!.Value.Ticks : StartDate!.Value.DayNumber;
    public long End => Kind == IntervalKind.Utc ? EndUtc!.Value.Ticks : EndDate!.Value.DayNumber;
    public bool Compatible(CoverageInterval other) => Kind == other.Kind && CalendarBasis == other.CalendarBasis;
    public CoverageInterval Slice(long start, long end) => Kind == IntervalKind.Utc ? new(Kind, new(start, DateTimeKind.Utc), new(end, DateTimeKind.Utc), null, null, null)
        : new(Kind, null, null, DateOnly.FromDayNumber(checked((int)start)), DateOnly.FromDayNumber(checked((int)end)), CalendarBasis);
}
public sealed record CoverageScope(Guid SourceId, Guid SportId, Guid CompetitionId, Guid SeasonId, Guid? ParticipantId,
    string EventType, ObservationType ObservationType, string CompetitionReference, string SeasonReference, CoverageInterval Interval)
{
    public void Validate()
    {
        if (SourceId == Guid.Empty || SportId == Guid.Empty || CompetitionId == Guid.Empty || SeasonId == Guid.Empty || ParticipantId == Guid.Empty ||
            EventType != "football-match" || ObservationType is not (ObservationType.EventDate or ObservationType.EventStatus) ||
            string.IsNullOrWhiteSpace(CompetitionReference) || CompetitionReference.Length > 50 || string.IsNullOrWhiteSpace(SeasonReference) || SeasonReference.Length > 50 || Interval is null)
            throw new ArgumentException("Explicit supported coverage dimensions required.");
        Interval.Validate();
    }
    public bool SameDimensions(CoverageScope other) => this with { Interval = other.Interval } == other;
}
public sealed class CoverageEvidence
{
    public Guid Id { get; init; }
    public Guid SourceId { get; init; }
    public required CoverageScope Scope { get; init; }
    public CoverageStatus Claim { get; init; }
    public CoverageBasis Basis { get; init; }
    public required string EvidenceReference { get; init; }
    public Guid RawId { get; init; }
    public required string RawHash { get; init; }
    public Guid PolicyId { get; init; }
    public Guid[] SupportingObservationIds { get; init; } = [];
    public int Version { get; init; }
    public DateTime? SourcePublishedAtUtc { get; init; }
    public DateTime RetrievedAtUtc { get; init; }
    public DateTime AvailableAtUtc { get; init; }
    public DateTime ValidUntilUtc { get; init; }
    public required string OperatorId { get; init; }
    public required string Reason { get; init; }
    [JsonInclude] public DateTime RecordedAtUtc { get; private set; }
}
public sealed class CoverageReview
{
    public Guid Id { get; init; }
    public Guid EvidenceId { get; init; }
    public Guid SourceId { get; init; }
    public int Sequence { get; init; }
    public CoverageReviewStatus Status { get; init; }
    public required string BasisReference { get; init; }
    public required string OperatorId { get; init; }
    public required string Reason { get; init; }
    public DateTime ReviewedAtUtc { get; init; }
    [JsonInclude] public DateTime RecordedAtUtc { get; private set; }
}
