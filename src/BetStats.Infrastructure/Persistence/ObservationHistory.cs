using BetStats.Application.Observations;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Persistence;

public sealed class ObservationHistory(BetStatsDbContext context) : IObservationHistory
{
    public async Task<IReadOnlyList<Observation>> ReadAsOfAsync(ObservationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var records = context.Observations.AsNoTracking().Where(item => item.EntityKind == query.EntityKind && item.AvailableAtUtc <= query.AsOfUtc);
        if (query.CanonicalId is { } id)
            records = query.EntityKind switch
            {
                CanonicalEntityKind.Sport => records.Where(item => item.CanonicalSportId == id),
                CanonicalEntityKind.Competition => records.Where(item => item.CanonicalCompetitionId == id),
                CanonicalEntityKind.Season => records.Where(item => item.CanonicalSeasonId == id),
                CanonicalEntityKind.Participant => records.Where(item => item.CanonicalParticipantId == id),
                CanonicalEntityKind.SportingEvent => records.Where(item => item.CanonicalSportingEventId == id),
                _ => throw new ArgumentException("Unknown entity kind.")
            };
        if (query.DataSourceId is { } source) records = records.Where(item => item.DataSourceId == source);
        if (query.ProviderIdentityId is { } identity) records = records.Where(item => item.ProviderIdentityId == identity);
        return await records.OrderBy(item => item.AvailableAtUtc).ThenBy(item => item.CreatedAtUtc).ThenBy(item => item.Id).ToListAsync(cancellationToken);
    }
}
