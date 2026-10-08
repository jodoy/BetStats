using BetStats.Domain.Observations;

namespace BetStats.Application.Observations;

public sealed record ObservationPageOptions
{
    public ObservationPageOptions(int maximumPageSize = 200)
    {
        if (maximumPageSize is < 1 or > 1000) throw new ArgumentException("Page cap must be between 1 and 1000.");
        MaximumPageSize = maximumPageSize;
    }
    public int MaximumPageSize { get; }
}

public sealed record ObservationCursor
{
    public ObservationCursor(ObservationQuery query, DateTime availableAtUtc, DateTime createdAtUtc, Guid id)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (availableAtUtc.Kind != DateTimeKind.Utc || createdAtUtc.Kind != DateTimeKind.Utc ||
            availableAtUtc.Ticks % 10 != 0 || createdAtUtc.Ticks % 10 != 0 || availableAtUtc > query.AsOfUtc || id == Guid.Empty)
            throw new ArgumentException("Cursor requires UTC ordering keys within the cutoff and a nonempty UUID.");
        Query = query; AvailableAtUtc = availableAtUtc; CreatedAtUtc = createdAtUtc; Id = id;
    }
    public ObservationQuery Query { get; }
    public DateTime AvailableAtUtc { get; }
    public DateTime CreatedAtUtc { get; }
    public Guid Id { get; }
}
public sealed record ObservationPage(IReadOnlyList<Observation> Items, ObservationCursor? NextCursor);
