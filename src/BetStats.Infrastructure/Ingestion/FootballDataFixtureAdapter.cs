using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Domain.Sports;

namespace BetStats.Infrastructure.Ingestion;

// Explicitly supplied fixtures only. There is deliberately no HTTP implementation.
public sealed class FootballDataFixtureAdapter(Guid sourceId, ReadOnlyMemory<byte> fixture, TimeProvider clock, bool liveEnabled = false) : IContentProviderAdapter
{
    public ProviderDescriptor Descriptor { get; } = new(sourceId, "football-data-csv-fixture", [ReferenceSports.All.Single(s => s.Code == "football").Id], [ProviderCapability.HistoricalObservations]);
    public RetrievedContent? Content { get; private set; }
    public ConfigurationValidation ValidateConfiguration() => new(!liveEnabled && fixture.Length is > 0 and <= 1_048_576,
        liveEnabled ? ["live_transport_not_authorized"] : fixture.Length is 0 or > 1_048_576 ? ["invalid_fixture_size"] : []);
    public Task<ProviderResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Content = null;
        if (!ValidateConfiguration().Valid) return Task.FromResult(new ProviderResult(false, new(ProviderErrorCategory.InvalidConfiguration, "invalid_fixture_configuration")));
        var now = clock.GetUtcNow().UtcDateTime; now = now.AddTicks(-(now.Ticks % 10));
        Content = new(fixture.ToArray(), "text/csv", now);
        return Task.FromResult(new ProviderResult(true));
    }
}
