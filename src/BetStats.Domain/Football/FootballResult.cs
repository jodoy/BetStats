namespace BetStats.Domain.Football;

public enum FootballMatchStatus { Scheduled, Live, HalfTime, Finished, Postponed, Cancelled, Abandoned }
public enum FootballResultPeriod { HalfTime, FullTime }
public enum FootballScoreBasis { Unknown, RegulationTime, IncludesExtraTime, PenaltyShootout }
public sealed record FootballScore(int? Home, int? Away);
public sealed record FootballResultValue(FootballMatchStatus Status, FootballScoreBasis Basis,
    FootballScore FullTime, FootballScore HalfTime);

// Provider identifiers are provenance on an observation, never canonical event keys.
public sealed class FootballResultObservation
{
    public Guid Id { get; init; }
    public Guid SourceId { get; init; }
    public Guid RawId { get; init; }
    public Guid ProviderIdentityId { get; init; }
    public Guid EventId { get; init; }
    public Guid CompetitionId { get; init; }
    public Guid SeasonId { get; init; }
    public Guid HomeId { get; init; }
    public Guid AwayId { get; init; }
    public Guid DateObservationId { get; init; }
    public string SourceEventReference { get; init; } = "";
    public string CompetitionReference { get; init; } = "";
    public string SeasonReference { get; init; } = "";
    public DateOnly EventDate { get; init; }
    public FootballResultValue Value { get; init; } = null!;
    public int SchemaVersion { get; init; } = 1;
    public int Version { get; init; }
    public Guid? CorrectsId { get; init; }
    public DateTime? PublishedAtUtc { get; init; }
    public DateTime RetrievedAtUtc { get; init; }
    public DateTime AvailableAtUtc { get; init; }
    [System.Text.Json.Serialization.JsonInclude]
    public DateTime RecordedAtUtc { get; private set; }
}
