using BetStats.Domain.Common;

namespace BetStats.Domain.Sports;

public sealed class Competition
{
    private Competition() { }
    public Competition(Guid id, Guid sportId, string name, string? countryCode, CompetitionType competitionType)
    {
        Id = Require.Id(id); SportId = Require.Id(sportId); Name = Require.Text(name, 200);
        Require.That(countryCode is null || (countryCode.Length == 2 && countryCode.All(character => character is >= 'A' and <= 'Z')),
            "Country code must be null or two uppercase letters.");
        CountryCode = countryCode; CompetitionType = Require.Defined(competitionType);
    }
    public Guid Id { get; private set; }
    public Guid SportId { get; private set; }
    public string Name { get; private set; } = null!;
    public string? CountryCode { get; private set; }
    public CompetitionType CompetitionType { get; private set; }
}

public enum CompetitionType { League, Tournament, Other }
