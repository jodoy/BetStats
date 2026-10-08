using BetStats.Domain.Sports;

namespace BetStats.Application.Sports;

public sealed record PublicSport(Guid Id, string Code);
public sealed record PublicSportPage(int Offset, int Limit, int Total, IReadOnlyList<PublicSport> Items);
public static class PublicSportCatalog
{
    // Project-owned reference vocabulary; never a projection of provider-derived tables.
    public static PublicSportPage Read(int offset, int limit)
    {
        if (offset < 0 || offset > 10000 || limit is < 1 or > 100) throw new ArgumentException("Offset must be 0..10000 and limit 1..100.");
        var items = ReferenceSports.All.OrderBy(s => s.Code, StringComparer.Ordinal).Select(s => new PublicSport(s.Id, s.Code)).ToArray();
        return new(offset, limit, items.Length, items.Skip(offset).Take(limit).ToArray());
    }
}
