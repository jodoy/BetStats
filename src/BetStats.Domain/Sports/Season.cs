using BetStats.Domain.Common;

namespace BetStats.Domain.Sports;

public sealed class Season
{
    private Season() { }
    public Season(Guid id, Guid competitionId, string name, DateOnly? startDate = null, DateOnly? endDate = null)
    {
        Id = Require.Id(id); CompetitionId = Require.Id(competitionId); Name = Require.Text(name, 100);
        Require.That(startDate is null || endDate is null || endDate >= startDate, "Season ends before it starts.");
        StartDate = startDate; EndDate = endDate;
    }
    public Guid Id { get; private set; }
    public Guid CompetitionId { get; private set; }
    public string Name { get; private set; } = null!;
    public DateOnly? StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }
}
