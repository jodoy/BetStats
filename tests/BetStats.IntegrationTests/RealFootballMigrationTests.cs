using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Football;
using BetStats.Application.Models;
using BetStats.Application.Ingestion;
using BetStats.Infrastructure.Ingestion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BetStats.IntegrationTests;

public sealed class RealFootballMigrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Upgrade_from_bs012_preserves_dataset_model_predictions_and_finalized_backtest_bytes()
    {
        await using var scenario = await new ResultOperationsWorkflowTests(fixture).Create();
        var db = scenario.Db;
        await db.GetService<IMigrator>().MigrateAsync("20261008231727_HistoricalBacktesting");
        var definition = await BacktestWorkflowTests.Definition(scenario);
        var model = new FootballModelDefinition(1, FootballModelKind.Poisson, new(), new());
        definition = definition with { Version = 2, Model = model, Predictor = model.Predictor };
        var built = await scenario.Get<IHistoricalBacktests>().RunAsync(new(Guid.NewGuid(), definition, "test:operator", "Finalize BS012 before upgrade", true));
        Assert.Equal(ResultOperationStatus.Succeeded, built.Status);
        var datasets = CanonicalDatasetJson.Serialize(await db.DatasetArtifacts.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync());
        var results = CanonicalDatasetJson.Serialize(await db.FootballResultArtifacts.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync());
        var backtests = CanonicalDatasetJson.Serialize(await db.Backtests.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync());
        var operations = CanonicalDatasetJson.Serialize(await db.BacktestOperations.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync());
        await db.Database.MigrateAsync(); db.ChangeTracker.Clear();
        var source = await SyntheticFootballDemo.PrepareAsync(db, true, "bs013-preservation-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(scenario.Root, "authorized-fiction.csv");
        await File.WriteAllTextAsync(file, "Div,Date,HomeTeam,AwayTeam,FTHG,FTAG,MatchId\nFICT,02/01/2026,Amber Comets,Cobalt Owls,1,0,preservation-event\n");
        var imports = scenario.Get<FootballImportOperations>();
        var plan = await imports.PlanAsync(source, file, SyntheticFootballDemo.Scope);
        Assert.Equal(ImportOutcome.Partial, (await imports.RunAsync(Guid.NewGuid(), plan, file, "test:operator", "Capture history after BS012 upgrade")).Outcome);
        Assert.Equal(datasets, CanonicalDatasetJson.Serialize(await db.DatasetArtifacts.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync()));
        Assert.Equal(results, CanonicalDatasetJson.Serialize(await db.FootballResultArtifacts.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync()));
        Assert.Equal(backtests, CanonicalDatasetJson.Serialize(await db.Backtests.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync()));
        Assert.Equal(operations, CanonicalDatasetJson.Serialize(await db.BacktestOperations.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync()));
        var verification = await scenario.Get<IHistoricalBacktests>().VerifyAsync(built.SnapshotId!.Value, true);
        Assert.True(verification.Integrity && verification.Reproducible && verification.CurrentlyAuthorized && verification.RawHashVerified == true);
        Assert.Equal(2, await db.FootballImportOperations.CountAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }
}
