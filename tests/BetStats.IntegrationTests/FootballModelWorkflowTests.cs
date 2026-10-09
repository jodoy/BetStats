using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Football;
using BetStats.Application.Models;
using BetStats.Infrastructure.Evaluation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BetStats.IntegrationTests;

public sealed class FootballModelWorkflowTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact] public async Task Inspection_and_simulation_are_read_only_and_disclose_unverified_zero_operational_samples()
    {
        await using var s = await new ResultOperationsWorkflowTests(fixture).Create(); var d = await BacktestWorkflowTests.Definition(s, true);
        var feature = await s.Get<IFootballResultDatasets>().InspectAsync(d.DatasetId); var row = feature.Manifest.Rows[0];
        var input = FootballModelInputs.From(row, BacktestRules.Input(row), feature.Manifest.MetadataManifest.Definition.SeasonId);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Model:Action"] = "inspect", ["Model:OperatorId"] = "operator", ["Model:Reason"] = "Explicit read only math",
            ["Model:DefinitionJson"] = "{\"Version\":1,\"Kind\":\"Poisson\",\"Elo\":{},\"Goals\":{}}",
            ["Model:InputJson"] = System.Text.Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(input)) }).Build();
        Assert.Equal(0, await ModelOperatorCommand.RunAsync(s.Scope.ServiceProvider, config, true));
        config["Model:Action"] = "simulate"; Assert.Equal(0, await ModelOperatorCommand.RunAsync(s.Scope.ServiceProvider, config, true));
        Assert.Empty(await s.Db.BacktestOperations.Where(e => e.Fingerprint == CanonicalDatasetJson.Fingerprint(d)).ToArrayAsync());
        Assert.Empty(await s.Db.Backtests.Where(e => e.DatasetId == d.DatasetId).ToArrayAsync());
    }
    [Fact] public async Task Bs011_database_and_finalized_artifacts_remain_byte_identical_after_models()
    {
        await using var s = await new ResultOperationsWorkflowTests(fixture).Create();
        Assert.Equal(14, (await s.Db.Database.GetAppliedMigrationsAsync()).Count()); Assert.False(s.Db.Database.HasPendingModelChanges()); Assert.Empty(await s.Db.Database.GetPendingMigrationsAsync());
        var d = await BacktestWorkflowTests.Definition(s); var ops = s.Get<IHistoricalBacktests>();
        var legacy = await ops.RunAsync(new(Guid.NewGuid(), d, "operator", "Finalize legacy BS011 before model operations", true)); Assert.Equal(ResultOperationStatus.Succeeded, legacy.Status);
        var artifact = await s.Db.Backtests.AsNoTracking().SingleAsync(a => a.Id == legacy.SnapshotId);
        var datasets = await s.Db.DatasetArtifacts.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync();
        var v3 = await s.Db.FootballResultArtifacts.AsNoTracking().SingleAsync(a => a.Id == d.DatasetId);
        var events = await s.Db.BacktestOperations.AsNoTracking().Where(a => a.OperationId == legacy.OperationId).OrderBy(a => a.Sequence).ToArrayAsync();
        var models = new List<BacktestManifest>();
        foreach (var kind in Enum.GetValues<FootballModelKind>())
        {
            var model = new FootballModelDefinition(1, kind, new(), new()); var md = d with { Version = 2, Model = model, Predictor = model.Predictor };
            var run = await ops.RunAsync(new(Guid.NewGuid(), md, "operator", "Explicit mathematical baseline model backtest", true)); Assert.Equal(ResultOperationStatus.Succeeded, run.Status);
            var snapshot = await ops.InspectAsync(run.SnapshotId!.Value); models.Add(snapshot.Manifest);
            Assert.All(snapshot.Manifest.Report.Targets, t => { Assert.Equal(0, t.Eligible); Assert.All(t.Metrics, m => Assert.Null(m.Value)); });
            Assert.NotNull(snapshot.Manifest.WalkForward); Assert.All(snapshot.Manifest.Predictions, p => Assert.NotNull(p.Model));
            var verified = await ops.VerifyAsync(snapshot.Id, true); Assert.True(verified.Integrity && verified.Reproducible && verified.CurrentlyAuthorized && verified.RawAvailable == true && verified.RawHashVerified == true);
        }
        var compare = ModelComparison.Compare(models); Assert.All(compare.EquivalentEligibleEvents.Values, Assert.Empty);
        s.Db.ChangeTracker.Clear(); var after = await s.Db.Backtests.AsNoTracking().SingleAsync(a => a.Id == artifact.Id);
        Assert.Equal(artifact.Content, after.Content); Assert.Equal(artifact.Hash, after.Hash); Assert.Equal(artifact.RecordedAtUtc, after.RecordedAtUtc);
        Assert.Equal(CanonicalDatasetJson.Serialize(events), CanonicalDatasetJson.Serialize(await s.Db.BacktestOperations.AsNoTracking().Where(a => a.OperationId == legacy.OperationId).OrderBy(a => a.Sequence).ToArrayAsync()));
        Assert.Equal(CanonicalDatasetJson.Serialize(datasets), CanonicalDatasetJson.Serialize(await s.Db.DatasetArtifacts.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync()));
        var unchanged = await s.Db.FootballResultArtifacts.AsNoTracking().SingleAsync(a => a.Id == v3.Id);
        Assert.Equal(v3.Content, unchanged.Content); Assert.Equal(v3.Hash, unchanged.Hash); Assert.Equal(v3.RecordedAtUtc, unchanged.RecordedAtUtc);
        var oldVerified = await ops.VerifyAsync(artifact.Id, true); Assert.True(oldVerified.Integrity && oldVerified.Reproducible && oldVerified.RawHashVerified == true);
    }
    [Fact] public async Task Model_worker_reuses_approval_and_development_gates()
    {
        await using var s = await new ResultOperationsWorkflowTests(fixture).Create(); var d = await BacktestWorkflowTests.Definition(s, true);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Model:Action"] = "backtest", ["Model:OperatorId"] = "operator", ["Model:Reason"] = "Explicit model operation", ["Model:OperationId"] = Guid.NewGuid().ToString(),
            ["Model:DefinitionJson"] = System.Text.Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(d)) }).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ModelOperatorCommand.RunAsync(s.Scope.ServiceProvider, config, false));
        await Assert.ThrowsAsync<ArgumentException>(() => ModelOperatorCommand.RunAsync(s.Scope.ServiceProvider, config, true));
        config["Model:Approve"] = "true"; Assert.Equal(0, await ModelOperatorCommand.RunAsync(s.Scope.ServiceProvider, config, true));
        var snapshot = await s.Db.Backtests.AsNoTracking().SingleAsync(a => a.DatasetId == d.DatasetId);
        config["Model:Action"] = "verify-deep"; config["Model:SnapshotId"] = snapshot.Id.ToString(); Assert.Equal(0, await ModelOperatorCommand.RunAsync(s.Scope.ServiceProvider, config, true));
    }
}
