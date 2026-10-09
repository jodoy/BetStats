using BetStats.Domain.Governance;

namespace BetStats.Application.Providers;

public enum FootballTransport { LocalCsv, Api }

// Capabilities describe technical support; they never grant legal permission.
public sealed record FootballProviderContract(int Version, FootballTransport Transport, string SchemaVersion,
    bool SupportsCorrections, bool SupportsInventory, int MaximumBytes, int MaximumPages)
{
    public static FootballProviderContract LocalHistory { get; } = new(1, FootballTransport.LocalCsv,
        "football-history-v1", true, false, 1_048_576, 1);
    public bool IsValid => Version == 1 && Enum.IsDefined(Transport) && !string.IsNullOrWhiteSpace(SchemaVersion) &&
        MaximumBytes is > 0 and <= 1_048_576 && MaximumPages is > 0 and <= 100;
    public static IReadOnlyList<DataPurpose> ImportPurposes { get; } = Array.AsReadOnly(new[] {
        DataPurpose.DataRetrieval, DataPurpose.RawPayloadStorage, DataPurpose.HistoricalRetention, DataPurpose.InternalAnalytics });
}

public sealed record FootballApiPageRequest(string? Cursor, int PageSize);
public sealed record FootballApiPage(ReadOnlyMemory<byte> Bytes, string? NextCursor, ProviderError? Error = null);

// Implementations must be explicitly configured and invoked through AuthorizedProviderExecutor.
// Each page/retry is a separate budgeted adapter execution. No transport is registered by default.
public interface IFootballApiTransport
{
    Task<FootballApiPage> ReadPageAsync(FootballApiPageRequest request, CancellationToken cancellationToken);
}

public sealed class FootballPagination(int maximumPages, int pageSize)
{
    private readonly HashSet<string> cursors = new(StringComparer.Ordinal);
    private int pages;
    public FootballApiPageRequest Next(string? cursor)
    {
        if (maximumPages is < 1 or > 100 || pageSize is < 1 or > 500 || pages >= maximumPages ||
            cursor is { Length: > 500 } || !cursors.Add(cursor ?? ""))
            throw new InvalidOperationException("Pagination bound or repeated cursor.");
        pages++;
        return new(cursor, pageSize);
    }
    public static bool Retryable(ProviderError error) => error.Category is ProviderErrorCategory.RateLimitExceeded
        or ProviderErrorCategory.Timeout or ProviderErrorCategory.TemporaryUnavailability;
}
