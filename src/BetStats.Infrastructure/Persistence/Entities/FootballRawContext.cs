namespace BetStats.Infrastructure.Persistence.Entities;

public sealed class FootballRawContext
{
    public Guid RawId { get; init; }
    public Guid SourceId { get; init; }
    public required string CompetitionReference { get; init; }
    public required string SeasonReference { get; init; }
    public int Version { get; init; } = 1;
    public DateTime RecordedAtUtc { get; private set; }
}
