using System.Text;
using BetStats.Application.Ingestion;
using BetStats.Domain.Identity;
using BetStats.Infrastructure;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BetStats.IntegrationTests;

public sealed class FootballHistoryImportTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    // Project-authored fictional rows in a football-data style export layout. No third-party data.
    private const string Header = "Div,Date,Time,HomeTeam,AwayTeam,FTHG,FTAG,FTR,HTHG,HTAG,HTR,B365H,B365D,B365A\n";

    private const string FirstSeason = Header +
        "XL,05/08/23,15:00,Amber Comets,Cobalt Owls,2,1,H,1,0,H,1.50,4.00,6.00\n" +
        "XL,12/08/23,15:00,Cobalt Owls,Silver Foxes,0,0,D,0,0,D,2.10,3.20,3.60\n" +
        "XM,12/08/23,15:00,Violet Herons,Amber Comets,1,1,D,0,0,D,2.00,3.00,4.00\n" +
        "XL,19/08/23,,Silver Foxes,Amber Comets,1,3,A,0,1,A,3.00,3.30,2.20\n";

    private const string SecondSeason = Header +
        "XL,10/08/24,15:00,Cobalt Owls,Amber Comets,1,1,D,1,0,H,2.50,3.10,2.90\n" +
        "XL,17/08/24,15:00,Amber Comets,Silver Foxes,4,0,H,2,0,H,1.40,4.50,7.00\n";

    private ServiceProvider Provider(string root) => new ServiceCollection().AddPersistence(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:BetStats"] = fixture.GetConnectionString(),
            ["Ingestion:RawStoragePath"] = Path.Combine(root, "raw"),
        }).Build()).BuildServiceProvider();

    private static FootballHistoryImportRequest Request(string root, string path, string season, string source) => new(
        path,
        Path.Combine(root, "work"),
        new FootballImportScope("XL", season),
        source,
        "Fictional history source",
        "test:project-authored-fixture",
        "Fictional Lantern League",
        null,
        season,
        "test:operator",
        "BS-020 fictional import test");

    [Fact]
    public async Task One_command_import_publishes_results_and_is_idempotent_across_runs_and_seasons()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs020-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var provider = Provider(root);
            using var scope = provider.CreateScope();
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<BetStatsDbContext>();
            var import = new FootballHistoryImport(db, services.GetRequiredService<FootballImportOperations>());
            var source = "bs020-" + Guid.NewGuid().ToString("N");

            var firstPath = Path.Combine(root, "first.csv");
            await File.WriteAllTextAsync(firstPath, FirstSeason, new UTF8Encoding(false));
            var first = await import.ImportAsync(Request(root, firstPath, "2023-2024", source));

            Assert.Equal(ImportOutcome.Succeeded, first.Import.Outcome);
            Assert.Equal(3, first.Import.AcceptedRecords);
            Assert.Equal(3, first.ProjectedRows);
            Assert.Equal(1, first.OtherCompetitionRows);
            Assert.True(first.Preparation.CreatedSource);
            Assert.Equal(3, first.Preparation.CreatedTeams);
            Assert.Equal(3, first.Preparation.CreatedEvents);

            var sourceId = first.Preparation.SourceId;
            var results = await db.FootballResults.AsNoTracking().Where(r => r.SourceId == sourceId).ToListAsync();
            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.NotNull(r.Value.FullTime.Home));

            var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.DataSourceId == sourceId);
            var stored = await services.GetRequiredService<IRawPayloadStore>()
                .ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, raw.ByteLength!.Value));
            Assert.Equal(await File.ReadAllBytesAsync(first.ProjectedPath), stored.ToArray());
            Assert.Equal(FirstSeason, await File.ReadAllTextAsync(firstPath));

            var repeated = await import.ImportAsync(Request(root, firstPath, "2023-2024", source));
            Assert.Equal(ImportOutcome.Reused, repeated.Import.Outcome);
            Assert.Equal(first.OperationId, repeated.OperationId);
            Assert.False(repeated.Preparation.CreatedSource);
            Assert.Equal(0, repeated.Preparation.CreatedEvents);
            Assert.Equal(3, repeated.Preparation.ExistingEvents);
            Assert.Equal(3, await db.FootballResults.CountAsync(r => r.SourceId == sourceId));
            Assert.Equal(3, await db.SportingEvents.CountAsync(e => e.SeasonId == first.Preparation.SeasonId));

            var secondPath = Path.Combine(root, "second.csv");
            await File.WriteAllTextAsync(secondPath, SecondSeason, new UTF8Encoding(false));
            var second = await import.ImportAsync(Request(root, secondPath, "2024-2025", source));

            Assert.Equal(ImportOutcome.Succeeded, second.Import.Outcome);
            Assert.Equal(first.Preparation.CompetitionId, second.Preparation.CompetitionId);
            Assert.NotEqual(first.Preparation.SeasonId, second.Preparation.SeasonId);
            Assert.Equal(0, second.Preparation.CreatedTeams);
            Assert.Equal(2, second.Preparation.CreatedEvents);

            var teamIdentities = await db.ProviderIdentities
                .CountAsync(i => i.DataSourceId == sourceId && i.EntityKind == CanonicalEntityKind.Participant);
            Assert.Equal(3, teamIdentities);
            Assert.Equal(5, await db.FootballResults.CountAsync(r => r.SourceId == sourceId));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Import_without_rows_for_the_competition_is_rejected_before_any_write()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs020-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var provider = Provider(root);
            using var scope = provider.CreateScope();
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<BetStatsDbContext>();
            var import = new FootballHistoryImport(db, services.GetRequiredService<FootballImportOperations>());
            var source = "bs020-" + Guid.NewGuid().ToString("N");
            var path = Path.Combine(root, "other.csv");
            await File.WriteAllTextAsync(path, Header + "XM,12/08/23,15:00,Violet Herons,Amber Comets,1,1,D,0,0,D,2,3,4\n");

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => import.ImportAsync(Request(root, path, "2023-2024", source)));

            Assert.Equal("no_rows_for_competition", error.Message);
            Assert.False(await db.DataSources.AnyAsync(s => s.Code == source));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
