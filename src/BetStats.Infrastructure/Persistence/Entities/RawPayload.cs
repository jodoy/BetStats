namespace BetStats.Infrastructure.Persistence.Entities;

public sealed class RawPayload
{
    public Guid Id { get; init; }
    public Guid DataSourceId { get; init; }
    public Guid? IngestionRunId { get; init; }
    public string? ExternalReference { get; init; }
    public DateTime RetrievedAtUtc { get; init; }
    public required string ContentHashSha256 { get; init; }
    public required string ContentType { get; init; }
    public required string StorageKey { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}
