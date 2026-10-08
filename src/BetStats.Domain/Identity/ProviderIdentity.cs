using BetStats.Domain.Common;

namespace BetStats.Domain.Identity;

public sealed class ProviderIdentity
{
    private ProviderIdentity() { }
    public ProviderIdentity(Guid id, Guid dataSourceId, CanonicalEntityKind entityKind, string externalId, DateTime createdAtUtc)
    {
        Id = Require.Id(id); DataSourceId = Require.Id(dataSourceId); EntityKind = Require.Defined(entityKind);
        ExternalId = Require.Text(externalId, 500); CreatedAtUtc = Require.Utc(createdAtUtc);
    }
    public Guid Id { get; private set; }
    public Guid DataSourceId { get; private set; }
    public CanonicalEntityKind EntityKind { get; private set; }
    public string ExternalId { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }
}
