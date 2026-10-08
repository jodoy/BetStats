using BetStats.Application.Football;
using BetStats.Application.Ingestion;
using BetStats.Domain.Governance;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Football;

public sealed class DevelopmentFootball(BetStatsDbContext db, IFootballResults results, IRawPayloadStore storage) : IDevelopmentFootball
{
    public async Task<DevelopmentFootballPage> ReadAsync(DateTime asOfUtc, int offset, int limit, Guid? eventId = null, bool history = false, CancellationToken token = default)
    {
        if (asOfUtc.Kind != DateTimeKind.Utc || asOfUtc.Ticks % 10 != 0 || offset is < 0 or > 10000 || limit is < 1 or > 100) throw new ArgumentException("UTC microsecond cutoff and offset 0..10000, limit 1..100 required.");
        var source = await db.DataSources.AsNoTracking().SingleOrDefaultAsync(s => s.Code == SyntheticFootballResultsDemo.SourceCode, token);
        if (source is null) return new(offset, limit, 0, []);
        var scopes = await db.FootballResults.AsNoTracking().Where(r => r.SourceId == source.Id).Select(r => new { r.CompetitionId, r.SeasonId }).Distinct().Take(2).ToListAsync(token);
        if (scopes.Count != 1) return new(offset, limit, 0, []);
        var scope = scopes[0];
        var report = await results.ReadAsync(new(scope.CompetitionId, scope.SeasonId, asOfUtc, DataPurpose.PublicDisplay, new(), eventId, source.Id, IncludeSuperseded: history), token);
        var output = new List<DevelopmentFootballEvent>();
        foreach (var evidence in report.Results.Where(e => e.Eligible))
        {
            var r = evidence.Observation; var raw = await db.RawPayloads.AsNoTracking().SingleAsync(x => x.Id == r.RawId, token);
            // The source code alone is insufficient: allow only byte-identical project-authored fixtures.
            if (!SyntheticFootballResultsDemo.PublicFixtureHashes.Contains(raw.ContentHashSha256) || raw.ByteLength is null) continue;
            var parsed = new FootballResultsCsvParser().Parse(await storage.ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, raw.ByteLength.Value), token), SyntheticFootballDemo.Scope, token);
            var row = parsed.Records.Single(x => x.MatchReference == r.SourceEventReference);
            var labels = evidence.Labels is { } l ? new FootballPublicLabels(l.Winner, l.FullTimeTotalGoals, l.BothTeamsScored, l.Over2_5Goals, l.HalfTimeTotalGoals) : null;
            output.Add(new(r.EventId, r.EventDate, row.HomeName, row.AwayName, new(r.Value.Status, r.Value.Basis, r.Value.FullTime, r.Value.HalfTime, r.AvailableAtUtc, labels)));
        }
        var ordered = output.OrderBy(x => x.Id).ThenBy(x => x.Result.AvailableAtUtc).ToArray();
        return new(offset, limit, ordered.Length, ordered.Skip(offset).Take(limit).ToArray());
    }
}
