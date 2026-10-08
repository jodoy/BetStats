namespace BetStats.Infrastructure.Persistence.Entities;

public sealed class DataSource
{
    public Guid Id { get; init; }
    public required string Code { get; set; }
    public required string DisplayName { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAtUtc { get; init; }
}
