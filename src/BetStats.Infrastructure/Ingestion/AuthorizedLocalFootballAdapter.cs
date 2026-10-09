using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Domain.Sports;

namespace BetStats.Infrastructure.Ingestion;

// The caller cannot read the file until the executor has authorized retrieval.
public sealed class AuthorizedLocalFootballAdapter(Guid sourceId, string path, TimeProvider clock,
    IFootballIngestionPersistence persistence) : IContentProviderAdapter
{
    public ProviderDescriptor Descriptor { get; } = new(sourceId, "authorized-football-history-v1",
        [ReferenceSports.All.Single(s => s.Code == "football").Id], [ProviderCapability.HistoricalObservations]);
    public RetrievedContent? Content { get; private set; }
    public ConfigurationValidation ValidateConfiguration() => new(!string.IsNullOrWhiteSpace(path), string.IsNullOrWhiteSpace(path) ? ["local_path_required"] : []);
    public async Task<ProviderResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        Content = null;
        await persistence.EnsureParsingAllowedAsync(sourceId, cancellationToken);
        // Bounded streaming read prevents a growing file from escaping the size limit.
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (file.Length is < 1 or > 1_048_576) return new(false, new(ProviderErrorCategory.InvalidResponse, "invalid_local_file_size"));
        using var buffer = new MemoryStream(); var chunk = new byte[8192];
        while (true)
        {
            var count = await file.ReadAsync(chunk, cancellationToken); if (count == 0) break;
            if (buffer.Length + count > 1_048_576) return new(false, new(ProviderErrorCategory.InvalidResponse, "invalid_local_file_size"));
            buffer.Write(chunk, 0, count);
        }
        await persistence.EnsureParsingAllowedAsync(sourceId, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime; now = now.AddTicks(-(now.Ticks % 10));
        Content = new(buffer.ToArray(), "text/csv", now, HistoricalFootballCsvParser.Version);
        return new(true);
    }
}
