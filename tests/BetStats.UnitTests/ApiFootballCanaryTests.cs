using BetStats.Application.Providers;

namespace BetStats.UnitTests;

public sealed class ApiFootballCanaryTests
{
    [Theory]
    [InlineData(1, 39, 2026, 0, 6, true)]
    [InlineData(1, 39, 2026, 0, 0, true)]
    [InlineData(2, 39, 2026, 0, 6, false)]
    [InlineData(1, 0, 2026, 0, 6, false)]
    [InlineData(1, 39, 1899, 0, 6, false)]
    [InlineData(1, 39, 2201, 0, 6, false)]
    [InlineData(1, 39, 2026, 0, 7, false)]
    [InlineData(1, 39, 2026, 1, 0, false)]
    public void Canary_requires_one_explicit_league_season_and_at_most_seven_days(int version, int league, int season, int from, int to, bool valid)
    {
        var day = new DateOnly(2026, 10, 13);
        Assert.Equal(valid, new ApiFootballCanary(version, league, season, day.AddDays(from), day.AddDays(to)).IsValid);
    }
}
