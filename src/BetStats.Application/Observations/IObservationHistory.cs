using BetStats.Domain.Identity;
using BetStats.Domain.Observations;

namespace BetStats.Application.Observations;

public sealed record ObservationQuery
{
    public ObservationQuery(CanonicalEntityKind entityKind, DateTime asOfUtc, Guid? canonicalId = null,
        Guid? dataSourceId = null, Guid? providerIdentityId = null)
    {
        if (!Enum.IsDefined(entityKind)) throw new ArgumentException("Unknown entity kind.");
        if (asOfUtc.Kind != DateTimeKind.Utc || asOfUtc.Ticks % 10 != 0) throw new ArgumentException("AsOfUtc must be UTC at microsecond precision.");
        if (canonicalId == Guid.Empty || dataSourceId == Guid.Empty || providerIdentityId == Guid.Empty) throw new ArgumentException("Filter UUIDs must not be empty.");
        EntityKind = entityKind; AsOfUtc = asOfUtc; CanonicalId = canonicalId; DataSourceId = dataSourceId; ProviderIdentityId = providerIdentityId;
    }
    public CanonicalEntityKind EntityKind { get; }
    public DateTime AsOfUtc { get; }
    public Guid? CanonicalId { get; }
    public Guid? DataSourceId { get; }
    public Guid? ProviderIdentityId { get; }
}

public interface IObservationHistory
{
    Task<IReadOnlyList<Observation>> ReadAsOfAsync(ObservationQuery query, CancellationToken cancellationToken = default);
    Task<ObservationPage> ReadPageAsOfAsync(ObservationQuery query, int pageSize, ObservationCursor? cursor = null, CancellationToken cancellationToken = default);
}
