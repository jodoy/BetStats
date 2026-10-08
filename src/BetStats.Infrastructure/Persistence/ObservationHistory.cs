using BetStats.Application.Observations;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Persistence;

public sealed class ObservationHistory(BetStatsDbContext context, ObservationPageOptions? options = null) : IObservationHistory
{
    private readonly ObservationPageOptions pageOptions = options ?? new();
    public async Task<IReadOnlyList<Observation>> ReadAsOfAsync(ObservationQuery query, CancellationToken cancellationToken = default)
    {
        var records = await Ordered(query, null).Take(pageOptions.MaximumPageSize + 1).ToListAsync(cancellationToken);
        if (records.Count > pageOptions.MaximumPageSize) throw new InvalidOperationException("Historical result exceeds the cap; use ReadPageAsOfAsync.");
        return records;
    }
    public async Task<ObservationPage> ReadPageAsOfAsync(ObservationQuery query, int pageSize, ObservationCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        if (pageSize < 1 || pageSize > pageOptions.MaximumPageSize) throw new ArgumentException("Page size exceeds the configured bounds.");
        if (cursor is not null && cursor.Query != query) throw new ArgumentException("Cursor does not belong to this query/cutoff.");
        var rows = await Ordered(query, cursor).Take(pageSize + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var last = rows.LastOrDefault();
        return new(rows, hasMore && last is not null ? new(query, last.AvailableAtUtc, last.CreatedAtUtc, last.Id) : null);
    }
    private IOrderedQueryable<Observation> Ordered(ObservationQuery query, ObservationCursor? cursor)
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
        if (cursor is not null)
            records = records.Where(item => EF.Functions.GreaterThan(ValueTuple.Create(item.AvailableAtUtc, item.CreatedAtUtc, item.Id),
                ValueTuple.Create(cursor.AvailableAtUtc, cursor.CreatedAtUtc, cursor.Id)));
        return records.OrderBy(item => item.AvailableAtUtc).ThenBy(item => item.CreatedAtUtc).ThenBy(item => item.Id);
    }
}
