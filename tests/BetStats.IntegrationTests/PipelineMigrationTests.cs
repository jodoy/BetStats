using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Football;
using BetStats.Application.Models;
using BetStats.Application.Pipeline;
using BetStats.Infrastructure.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BetStats.IntegrationTests;

public sealed class PipelineMigrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact] public async Task Upgrade_from_bs014_and_pipeline_operation_preserve_all_finalized_legacy_artifact_bytes()
    {
        await using var s = await new ResultOperationsWorkflowTests(fixture).Create();
        await s.Db.GetService<IMigrator>().MigrateAsync("20261009102627_RealFootballImportOperations");
        var definition = await BacktestWorkflowTests.Definition(s);
        foreach (var kind in Enum.GetValues<FootballModelKind>())
        {
            var model = new FootballModelDefinition(1, kind, new(), new());
            var run = await s.Get<IHistoricalBacktests>().RunAsync(new(Guid.NewGuid(), definition with { Version = 2, Model = model, Predictor = model.Predictor }, "test:operator", "Freeze existing baseline before BS015 upgrade", true));
            Assert.Equal(ResultOperationStatus.Succeeded, run.Status);
        }
        var metadata = CanonicalDatasetJson.Serialize(await s.Db.DatasetArtifacts.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync());
        var results = CanonicalDatasetJson.Serialize(await s.Db.FootballResultArtifacts.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync());
        var predictions = CanonicalDatasetJson.Serialize(await s.Db.Backtests.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync());
        var operations = CanonicalDatasetJson.Serialize(await s.Db.BacktestOperations.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync());
        await s.Db.Database.MigrateAsync(); s.Db.ChangeTracker.Clear();
        var jobs = s.Get<PostgreSqlPipeline>(); var approval = new PipelineApproval("audit:operator", "Explicit migration preservation verification", true);
        await jobs.PlanAsync(Guid.NewGuid(), new(1, PipelineKind.LocalSynchronization, new(await s.Now()), "{}"), approval);
        Assert.Equal(metadata, CanonicalDatasetJson.Serialize(await s.Db.DatasetArtifacts.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync()));
        Assert.Equal(results, CanonicalDatasetJson.Serialize(await s.Db.FootballResultArtifacts.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync()));
        Assert.Equal(predictions, CanonicalDatasetJson.Serialize(await s.Db.Backtests.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync()));
        Assert.Equal(operations, CanonicalDatasetJson.Serialize(await s.Db.BacktestOperations.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync()));
        Assert.False(s.Db.Database.HasPendingModelChanges()); Assert.Empty(await s.Db.Database.GetPendingMigrationsAsync());
    }
}
