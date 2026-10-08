using System.Globalization;
using BetStats.Application.Datasets;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Quality;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Datasets;

public static class SyntheticDatasetDemo
{
    public static async Task<(Guid SourceId, DatasetDefinition Definition)> PrepareAsync(BetStatsDbContext db, FootballIngestion ingestion,
        RequestBudget budget, TimeProvider clock, bool approve, string sourceCode, DateOnly targetDate, CancellationToken token = default)
    {
        var source = await SyntheticFootballDemo.PrepareAsync(db, approve, sourceCode, token);
        var now = await QualityPersistence.Now(db, token);
        if (targetDate <= DateOnly.FromDateTime(now) || targetDate.Year != 2026) throw new ArgumentException("Explicit future fictional target date in 2026 required.");
        var competitionId = (await db.IdentityResolutions.SingleAsync(d => db.ProviderIdentities.Any(i => i.Id == d.ProviderIdentityId && i.DataSourceId == source && i.EntityKind == CanonicalEntityKind.Competition), token)).CanonicalCompetitionId!.Value;
        var seasonId = (await db.IdentityResolutions.SingleAsync(d => db.ProviderIdentities.Any(i => i.Id == d.ProviderIdentityId && i.DataSourceId == source && i.EntityKind == CanonicalEntityKind.Season), token)).CanonicalSeasonId!.Value;
        var competition = await db.Competitions.SingleAsync(c => c.Id == competitionId, token);
        var season = await db.Seasons.SingleAsync(s => s.Id == seasonId, token);
        async Task<Participant> Team(string name)
        {
            var identity = await db.ProviderIdentities.SingleAsync(i => i.DataSourceId == source && i.ExternalId == FootballDataCsvParser.TeamReference("FICT", name), token);
            var decision = await QualityPersistence.Decision(db, identity.Id, now, token);
            return await db.Participants.SingleAsync(p => p.Id == decision!.CanonicalParticipantId, token);
        }
        var home = await Team("Amber Comets"); var away = await Team("Cobalt Owls");
        foreach (var reference in new[] { "dataset-target", "dataset-missing-status" })
        {
            var existing = await QualityPersistence.Anchor(db, source, CanonicalEntityKind.SportingEvent, "provider:" + reference, token);
            if (existing is not null) continue;
            var sportingEvent = new SportingEvent(Guid.NewGuid(), competition, season, null, SportingEventStatus.Scheduled, now);
            sportingEvent.AddParticipant(home, ParticipantRole.Home, 1); sportingEvent.AddParticipant(away, ParticipantRole.Away, 2);
            var identity = new ProviderIdentity(Guid.NewGuid(), source, CanonicalEntityKind.SportingEvent, "provider:" + reference, now);
            db.AddRange(sportingEvent, identity, new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Resolved,
                new(CanonicalEntityKind.SportingEvent, sportingEvent.Id), "operator:synthetic-dataset", "Exact fictional fixture context", now));
        }
        await db.SaveChangesAsync(token);
        var csv = "Div,Date,HomeTeam,AwayTeam,FTR,MatchId\n" +
            "FICT,02/01/2026,Amber Comets,Cobalt Owls,H,dataset-prior-1\n" +
            "FICT,05/01/2026,Amber Comets,Silver Foxes,D,dataset-prior-2\n" +
            "FICT,06/01/2026,Silver Foxes,Cobalt Owls,A,dataset-prior-3\n" +
            "FICT,07/01/2026,Amber Comets,Cobalt Owls,,dataset-missing-status\n" +
            "FICT," + targetDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + ",Amber Comets,Cobalt Owls,,dataset-target\n" +
            "FICT," + targetDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + ",Silver Foxes,Violet Herons,H,dataset-same-day\n";
        var imported = await ingestion.RunAsync(new FootballDataFixtureAdapter(source, SyntheticFootballDemo.Bytes(csv), clock), SyntheticFootballDemo.Scope, budget, token);
        if (imported.Outcome is not (ImportOutcome.Succeeded or ImportOutcome.Reused)) throw new InvalidOperationException("Synthetic dataset fixture publication failed.");
        now = await QualityPersistence.Now(db, token);
        var target = await db.Observations.AsNoTracking().Where(o => o.DataSourceId == source && o.Type == BetStats.Domain.Observations.ObservationType.EventDate &&
            db.ProviderIdentities.Any(i => i.Id == o.ProviderIdentityId && i.ExternalId == "provider:dataset-target")).OrderByDescending(o => o.Version).FirstAsync(token);
        return (source, new(1, competition.SportId, competitionId, seasonId, "FICT", "2026-fiction", new(2026, 1, 1), new(2026, 12, 31),
            now, DatasetMode.HistoricalAsKnown, null, 1, 1, DataPurpose.InternalAnalytics, new(), "UTC-calendar", "eligible-observed-metadata-v1", "fail-closed-v1", [new(target.Id, now)]));
    }
}
