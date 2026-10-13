namespace BetStats.Application.Providers;

// Technical scope only. This does not approve a provider or its data rights.
public sealed record ApiFootballCanary(int Version, int League, int Season, DateOnly From, DateOnly To)
{
    public bool IsValid => Version == 1 && League > 0 && Season is >= 1900 and <= 2200 &&
        To >= From && To.DayNumber - From.DayNumber <= 6;
}
