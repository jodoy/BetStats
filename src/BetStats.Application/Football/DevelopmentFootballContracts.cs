using BetStats.Domain.Football;

namespace BetStats.Application.Football;

public sealed record DevelopmentFootballResult(FootballMatchStatus Status, FootballScoreBasis Basis,
    FootballScore FullTime, FootballScore HalfTime, DateTime AvailableAtUtc, FootballPublicLabels? Labels);
public sealed record FootballPublicLabels(string? Winner, int? FullTimeTotalGoals, bool? BothTeamsScored, bool? Over2_5Goals, int? HalfTimeTotalGoals);
public sealed record DevelopmentFootballEvent(Guid Id, DateOnly Date, string Home, string Away, DevelopmentFootballResult Result);
public sealed record DevelopmentFootballPage(int Offset, int Limit, int Total, IReadOnlyList<DevelopmentFootballEvent> Items);
public interface IDevelopmentFootball
{
    Task<DevelopmentFootballPage> ReadAsync(DateTime asOfUtc, int offset, int limit, Guid? eventId = null, bool history = false, CancellationToken token = default);
}
