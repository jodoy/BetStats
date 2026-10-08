using System.Text;
using BetStats.Application.Ingestion;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Ingestion;

public static class SyntheticFootballDemo
{
    public static readonly FootballImportScope Scope = new("FICT", "2026-fiction");
    public const string Csv = "Div,Date,HomeTeam,AwayTeam,FTR,MatchId\nFICT,02/01/2026,Amber Comets,Cobalt Owls,H,fictional-1\nFICT,04/01/2026,Silver Foxes,Violet Herons,D,fictional-2\nFICT,05/01/2026,Amber Comets,Silver Foxes,A,fictional-3\nFICT,invalid,Cobalt Owls,Violet Herons,H,fictional-invalid\nFICT,02/01/2026,Amber Comets,Cobalt Owls,H,fictional-1\nFICT,06/01/2026,Unmapped Ravens,Violet Herons,H,fictional-unresolved\n";
    public const string CorrectionCsv = "Div,Date,HomeTeam,AwayTeam,FTR,MatchId\nFICT,03/01/2026,Amber Comets,Cobalt Owls,H,fictional-1\n";
    public static byte[] Bytes(string csv = Csv) => Encoding.UTF8.GetBytes(csv);

    // Separate explicit operator setup. Never called by ingestion or host startup.
    public static async Task<Guid> PrepareAsync(BetStatsDbContext context, bool approveSynthetic, string sourceCode = "synthetic-football-demo", CancellationToken cancellationToken = default)
    {
        if (!approveSynthetic) throw new InvalidOperationException("Explicit synthetic policy and mapping approval is required.");
        var existing = await context.DataSources.SingleOrDefaultAsync(s => s.Code == sourceCode, cancellationToken);
        if (existing is not null) return existing.Id;
        var now = await context.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
        var source = new DataSource { Id = Guid.NewGuid(), Code = sourceCode, DisplayName = "Fictional football fixture only", IsEnabled = true, CreatedAtUtc = now };
        var football = ReferenceSports.All.Single(s => s.Code == "football").Id;
        var competition = new Competition(Guid.NewGuid(), football, "Fictional Lantern League", null, CompetitionType.League);
        var season = new Season(Guid.NewGuid(), competition.Id, "Fictional 2026 season");
        var policy = new SourcePolicy(Guid.NewGuid(), source.Id, 1, now, null, "synthetic:owned-fixture", "synthetic:operator-review", now,
            new[] { DataPurpose.DataRetrieval, DataPurpose.RawPayloadStorage, DataPurpose.HistoricalRetention, DataPurpose.InternalAnalytics }.Select(p => new PurposePermission(p, PermissionDecision.Allowed)));
        context.AddRange(source, competition, season, policy);
        Reviewed(CanonicalEntityKind.Competition, "provider:competition:FICT", competition.Id);
        Reviewed(CanonicalEntityKind.Season, FootballDataCsvParser.SeasonReference(Scope.CompetitionReference, Scope.SeasonReference), season.Id);
        foreach (var name in new[] { "Amber Comets", "Cobalt Owls", "Silver Foxes", "Violet Herons" })
        {
            var team = new Participant(Guid.NewGuid(), football, name, ParticipantType.Team); context.Add(team);
            Reviewed(CanonicalEntityKind.Participant, FootballDataCsvParser.TeamReference(Scope.CompetitionReference, name), team.Id);
        }
        await context.SaveChangesAsync(cancellationToken);
        policy.Approve(Guid.NewGuid(), "operator:explicit-synthetic-review", "Only project-authored fictional fixtures; no candidate provider grant", now, now);
        await context.SaveChangesAsync(cancellationToken); return source.Id;
        void Reviewed(CanonicalEntityKind kind, string reference, Guid target)
        {
            var identity = new ProviderIdentity(Guid.NewGuid(), source.Id, kind, reference, now); context.Add(identity);
            context.Add(new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Resolved, new(kind, target), "operator:explicit-synthetic-review", "Exact fictional fixture mapping", now));
        }
    }
}
