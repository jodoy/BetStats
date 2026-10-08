namespace BetStats.Infrastructure.Persistence.Entities;

public sealed class IngestionRun
{
    public Guid Id { get; init; }
    public Guid DataSourceId { get; init; }
    public IngestionRunStatus Status { get; set; } = IngestionRunStatus.Pending;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? ErrorCode { get; set; }
    public DateTime CreatedAtUtc { get; init; }
}

public enum IngestionRunStatus
{
    Pending,
    Running,
    Succeeded,
    Failed
}
