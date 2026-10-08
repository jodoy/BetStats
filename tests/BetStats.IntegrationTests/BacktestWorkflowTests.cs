using BetStats.Application.Coverage;
using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Football;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;
using BetStats.Infrastructure.Evaluation;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BetStats.IntegrationTests;

public sealed class BacktestWorkflowTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private Task<ResultOperationsWorkflowTests.Scenario> Create() => new ResultOperationsWorkflowTests(fixture).Create();
    private static async Task<BacktestDefinition> Definition(ResultOperationsWorkflowTests.Scenario s)
    {
        var dataset = await s.Get<IResultDatasetOperations>().BuildAsync(new(Guid.NewGuid(), await s.Request(), "operator", "Freeze BS011 fictional feature input", true));
        Assert.Equal(ResultOperationStatus.Succeeded, dataset.Status);
        var snapshot = await s.Get<IFootballResultDatasets>().InspectAsync(dataset.SnapshotId!.Value);
        var evaluations = Enum.GetValues<EvaluationTarget>().Select(t => new EvaluationDefinition(3, snapshot.Manifest.MetadataManifest.Definition.SportId, t,
            DatasetMode.HistoricalAsKnown, null, PredictionCutoffPolicy.BeforeCalendarDay, TimeSpan.FromHours(1),
            ["features", "event-time", "quality", "coverage", "source-policy"], t.ToString(), 1,
            new("result", 1, [ObservationType.EventDate], 30, false, "Completed", 1, false, true),
            EvaluationContracts.Metrics.Where(m => BacktestRules.Count(t) ? m.Name == "mae" : t == EvaluationTarget.MatchWinner ? m.Name != "mae" && m.Name != "calibration_error" : m.Name != "mae").ToArray())).ToArray();
        return new(1, snapshot.Id, snapshot.Hash, "synthetic-constant", 1, await s.Now(), evaluations);
    }
    [Fact] public async Task Planning_is_read_only_and_running_freezes_honest_empty_metrics()
    {
        await using var s = await Create(); var d = await Definition(s); var ops = s.Get<IHistoricalBacktests>();
        var datasetBefore = await s.Db.FootballResultArtifacts.AsNoTracking().SingleAsync(a => a.Id == d.DatasetId);
        var plan = await ops.PlanAsync(d); Assert.Empty(await s.Db.BacktestOperations.Where(e => e.Fingerprint == CanonicalDatasetJson.Fingerprint(d)).ToArrayAsync()); Assert.Empty(await s.Db.Backtests.Where(a => a.DatasetId == d.DatasetId).ToArrayAsync());
        Assert.Equal(6, plan.Predictions.Count); Assert.All(plan.Samples, x => Assert.False(x.Eligible));
        Assert.All(plan.Report.Targets, x => { Assert.Equal(0, x.Eligible); Assert.Equal(1, x.Excluded); Assert.Contains("independent_complete_result_coverage_required", x.ExclusionReasons.Keys); });
        var operation = Guid.NewGuid(); var r = await ops.RunAsync(new(operation, d, "operator", "Explicit fictional simulation", true)); Assert.Equal(ResultOperationStatus.Succeeded, r.Status);
        var snapshot = await ops.InspectAsync(r.SnapshotId!.Value); Assert.Equal(CanonicalDatasetJson.Serialize(plan), CanonicalDatasetJson.Serialize(snapshot.Manifest));
        Assert.True(snapshot.RecordedAtUtc > snapshot.Manifest.Predictions[0].PredictionCutoffUtc);
        var v = await ops.VerifyAsync(snapshot.Id, true); Assert.True(v.Integrity && v.Reproducible && v.CurrentlyAuthorized && v.RawAvailable == true && v.RawHashVerified == true);
        var replay = await ops.RunAsync(new(operation, d, "operator:retry", "Exact replay", true)); Assert.Equal(r.SnapshotId, replay.SnapshotId); Assert.Equal(r.Sequence, replay.Sequence);
        var same = await ops.RunAsync(new(Guid.NewGuid(), d, "operator", "Independent identical content", true)); Assert.Equal(r.SnapshotId, same.SnapshotId);
        var after = await s.Db.FootballResultArtifacts.AsNoTracking().SingleAsync(a => a.Id == d.DatasetId);
        Assert.Equal(datasetBefore.Content, after.Content); Assert.Equal(datasetBefore.Hash, after.Hash); Assert.Equal(datasetBefore.RecordedAtUtc, after.RecordedAtUtc);
    }
    [Fact] public async Task Concurrent_runs_publish_one_artifact_and_one_terminal_append()
    {
        await using var s = await Create(); var d = await Definition(s); var request = new BacktestRequest(Guid.NewGuid(), d, "operator", "Concurrent requests", true);
        using var left = s.Provider.CreateScope(); using var right = s.Provider.CreateScope();
        var reports = await Task.WhenAll(left.ServiceProvider.GetRequiredService<IHistoricalBacktests>().RunAsync(request), right.ServiceProvider.GetRequiredService<IHistoricalBacktests>().RunAsync(request));
        Assert.Contains(reports, r => r.Status == ResultOperationStatus.Succeeded);
        Assert.Equal(1, await s.Db.BacktestOperations.CountAsync(e => e.OperationId == request.OperationId && e.Status == ResultOperationStatus.Succeeded));
        Assert.Equal(1, await s.Db.Backtests.CountAsync(a => a.DatasetId == d.DatasetId)); Assert.Equal(3, await s.Db.BacktestOperations.CountAsync(e => e.OperationId == request.OperationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<IHistoricalBacktests>().RunAsync(request with { Definition = d with { EvaluationCutoffUtc = d.EvaluationCutoffUtc.AddTicks(10) } }));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Missing_or_corrupt_raw_fails_run_and_can_be_explicitly_recovered(bool corrupt)
    {
        await using var s = await Create(); var d = await Definition(s); var ops = s.Get<IHistoricalBacktests>(); var raw = await s.Db.RawPayloads.FirstAsync(r => r.DataSourceId == s.Source);
        var path = Path.Combine(s.Root, raw.StorageKey + ".raw"); var bytes = await File.ReadAllBytesAsync(path);
        if (corrupt) await File.WriteAllTextAsync(path, "damaged"); else File.Delete(path);
        var operation = Guid.NewGuid(); var failed = await ops.RunAsync(new(operation, d, "operator", "Known damaged input", true)); Assert.Equal(ResultOperationStatus.Failed, failed.Status); Assert.Null(failed.SnapshotId);
        await File.WriteAllBytesAsync(path, bytes);
        var recovered = await ops.RecoverAsync(new(operation, failed.Fingerprint, "operator", "RAW restored and explicitly approved", true)); Assert.Equal(ResultOperationStatus.Succeeded, recovered.Status);
        Assert.Equal(1, await s.Db.Backtests.CountAsync(a => a.DatasetId == d.DatasetId));
        if (corrupt) await File.WriteAllTextAsync(path, "damaged-again"); else File.Delete(path);
        var verify = await ops.VerifyAsync(recovered.SnapshotId!.Value, true); Assert.True(verify.Integrity && verify.Reproducible); Assert.False(verify.RawHashVerified);
        if (!corrupt) Assert.False(verify.RawAvailable);
    }
    [Fact] public async Task Revocation_denies_plan_run_inspection_replay_recovery_and_raw_read()
    {
        await using var s = await Create(); var d = await Definition(s); var ops = s.Get<IHistoricalBacktests>(); var operation = Guid.NewGuid();
        var built = await ops.RunAsync(new(operation, d, "operator", "Before revoke", true)); Assert.Equal(ResultOperationStatus.Succeeded, built.Status);
        var policy = await s.Db.SourcePolicies.Include(p => p.Audit).Include(p => p.Permissions).SingleAsync(p => p.DataSourceId == s.Source);
        policy.Revoke(Guid.NewGuid(), "operator", "Explicit usage revocation", await s.Now()); await s.Db.SaveChangesAsync();
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => ops.PlanAsync(d));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => ops.InspectAsync(built.SnapshotId!.Value));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => ops.RunAsync(new(operation, d, "operator", "Denied replay", true)));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => ops.RecoverAsync(new(operation, built.Fingerprint, "operator", "Denied finalized recovery", true)));
        var v = await ops.VerifyAsync(built.SnapshotId!.Value, true); Assert.True(v.Integrity); Assert.False(v.CurrentlyAuthorized); Assert.Null(v.RawAvailable);
        var denied = await ops.RunAsync(new(Guid.NewGuid(), d, "operator", "Denied new operation", true)); Assert.Equal(ResultOperationStatus.Failed, denied.Status);
    }
    private static async Task<BacktestOperationEvent> Interrupted(ResultOperationsWorkflowTests.Scenario s, BacktestDefinition d, Guid operation, TimeSpan lease)
    {
        var bytes = CanonicalDatasetJson.Serialize(d); var fingerprint = CanonicalDatasetJson.Hash(bytes);
        s.Db.Add(new BacktestOperationEvent { Id = Guid.NewGuid(), OperationId = operation, Sequence = 1, Status = ResultOperationStatus.Requested,
            Fingerprint = fingerprint, Request = bytes, OwnerToken = Guid.Empty, OperatorId = "operator", Reason = "Recorded interrupted operation" }); await s.Db.SaveChangesAsync();
        var running = new BacktestOperationEvent { Id = Guid.NewGuid(), OperationId = operation, Sequence = 2, Status = ResultOperationStatus.Running,
            Fingerprint = fingerprint, Request = bytes, OwnerToken = Guid.NewGuid(), LeaseUntilUtc = (await s.Now()).Add(lease), OperatorId = "operator", Reason = "Recorded stopped owner" };
        s.Db.Add(running); await s.Db.SaveChangesAsync(); return running;
    }
    [Fact] public async Task Recovery_cannot_steal_live_owner_and_fences_stale_terminal_writes()
    {
        await using var s = await Create(); var d = await Definition(s); var operation = Guid.NewGuid(); var running = await Interrupted(s, d, operation, TimeSpan.FromSeconds(2));
        var ops = s.Get<IHistoricalBacktests>(); var recovery = new ResultRecoveryRequest(operation, running.Fingerprint, "operator", "Explicit recovery", true);
        Assert.Equal(ResultOperationStatus.Running, (await ops.RecoverAsync(recovery)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.RecoverAsync(recovery with { ExpectedFingerprint = new('a', 64) }));
        await Task.Delay(2100);
        await using (var expired = fixture.CreateContext())
        {
            expired.Add(new BacktestOperationEvent { Id = Guid.NewGuid(), OperationId = operation, Sequence = 3, Status = ResultOperationStatus.Failed,
                Fingerprint = running.Fingerprint, Request = running.Request, OwnerToken = running.OwnerToken, OperatorId = "operator", Reason = "Expired owner before recovery" });
            await Assert.ThrowsAsync<DbUpdateException>(() => expired.SaveChangesAsync());
        }
        using var left = s.Provider.CreateScope(); using var right = s.Provider.CreateScope();
        var reports = await Task.WhenAll(left.ServiceProvider.GetRequiredService<IHistoricalBacktests>().RecoverAsync(recovery), right.ServiceProvider.GetRequiredService<IHistoricalBacktests>().RecoverAsync(recovery));
        Assert.Contains(reports, r => r.Status == ResultOperationStatus.Succeeded);
        Assert.Equal(2, await s.Db.BacktestOperations.CountAsync(e => e.OperationId == operation && e.Status == ResultOperationStatus.Running));
        Assert.Equal(1, await s.Db.BacktestOperations.CountAsync(e => e.OperationId == operation && e.Status == ResultOperationStatus.Succeeded));
        await using var stale = new BetStatsDbContext(new DbContextOptionsBuilder<BetStatsDbContext>().UseNpgsql(s.Db.Database.GetConnectionString()).Options);
        stale.Add(new BacktestOperationEvent { Id = Guid.NewGuid(), OperationId = operation, Sequence = 5, Status = ResultOperationStatus.Failed,
            Fingerprint = running.Fingerprint, Request = running.Request, OwnerToken = running.OwnerToken, OperatorId = "operator", Reason = "Rejected stale owner" });
        await Assert.ThrowsAsync<DbUpdateException>(() => stale.SaveChangesAsync());
    }
    [Fact] public async Task Cancellation_after_claim_is_recoverable_without_partial_publication()
    {
        await using var s = await Create(); var d = await Definition(s); var operation = Guid.NewGuid();
        await using var blocker = fixture.CreateContext(); await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM ingestion.\"DataSources\" WHERE \"Id\"={s.Source} FOR UPDATE");
        using var scope = s.Provider.CreateScope(); using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = scope.ServiceProvider.GetRequiredService<IHistoricalBacktests>().RunAsync(new(operation, d, "operator", "Cancel durable operation", true), cancellation.Token);
        for (var i = 0; i < 100 && !await s.Db.BacktestOperations.AnyAsync(e => e.OperationId == operation && e.Status == ResultOperationStatus.Running); i++) await Task.Delay(20);
        Assert.True(await s.Db.BacktestOperations.AnyAsync(e => e.OperationId == operation && e.Status == ResultOperationStatus.Running));
        cancellation.Cancel(); var cancelled = await run; Assert.Equal(ResultOperationStatus.Cancelled, cancelled.Status); Assert.Empty(await s.Db.Backtests.Where(a => a.DatasetId == d.DatasetId).ToArrayAsync());
        await tx.RollbackAsync(); Assert.Equal(ResultOperationStatus.Succeeded, (await s.Get<IHistoricalBacktests>().RecoverAsync(new(operation, cancelled.Fingerprint, "operator", "Explicitly resume cancelled work", true))).Status);
    }
    [Fact] public async Task Later_history_correction_never_changes_frozen_prediction_inputs()
    {
        await using var s = await Create(); var d = await Definition(s); var ops = s.Get<IHistoricalBacktests>(); var one = await ops.PlanAsync(d);
        Assert.Equal(BetStats.Application.Ingestion.ImportOutcome.Succeeded, (await s.Import(s.Fixture.Correction)).Outcome);
        var two = await ops.PlanAsync(d with { EvaluationCutoffUtc = await s.Now() });
        Assert.Equal(CanonicalDatasetJson.Serialize(one.Predictions), CanonicalDatasetJson.Serialize(two.Predictions));
        Assert.NotEqual(one.Fingerprint, two.Fingerprint);
    }
    [Fact] public async Task Operator_commands_enforce_development_and_mutation_approval()
    {
        await using var s = await Create(); var d = await Definition(s); var values = new Dictionary<string, string?> { ["Backtest:Action"] = "run", ["Backtest:OperatorId"] = "operator",
            ["Backtest:Reason"] = "Explicit Worker invocation", ["Backtest:OperationId"] = Guid.NewGuid().ToString(), ["Backtest:DefinitionJson"] = System.Text.Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(d)) };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => BacktestOperatorCommand.RunAsync(s.Scope.ServiceProvider, config, false));
        await Assert.ThrowsAsync<ArgumentException>(() => BacktestOperatorCommand.RunAsync(s.Scope.ServiceProvider, config, true));
        Assert.Empty(await s.Db.BacktestOperations.Where(e => e.Fingerprint == CanonicalDatasetJson.Fingerprint(d)).ToArrayAsync()); config["Backtest:Approve"] = "true";
        Assert.Equal(0, await BacktestOperatorCommand.RunAsync(s.Scope.ServiceProvider, config, true));
    }
    [Theory] [InlineData("evaluation.\"Backtests\"")] [InlineData("evaluation.\"BacktestOperations\"")]
    public async Task Ordinary_sql_cannot_update_delete_or_truncate_backtest_history(string table)
    {
        await using var db = fixture.CreateContext();
        foreach (var sql in new[] { "UPDATE " + table + " SET \"Id\"=\"Id\"", "DELETE FROM " + table, "TRUNCATE " + table + " CASCADE" })
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
    }
    [Fact] public async Task Actual_worker_routes_plan_run_inspect_verify_and_finalized_recovery()
    {
        await using var s = await Create(); var d = await Definition(s); var operation = Guid.NewGuid();
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "BetStats.slnx"))) repo = repo.Parent;
        Assert.NotNull(repo);
        async Task<System.Text.Json.JsonElement> Invoke(string action, Guid? snapshot = null, string? fingerprint = null)
        {
            var start = new System.Diagnostics.ProcessStartInfo("dotnet") { WorkingDirectory = repo.FullName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(Path.Combine(repo.FullName, "src/BetStats.Worker/bin/Release/net10.0/BetStats.Worker.dll"));
            foreach (var arg in new[] { "--Logging:LogLevel:Default=Warning", "--Backtest:Action=" + action, "--Backtest:OperatorId=operator:test", "--Backtest:Reason=Explicit Worker backtest",
                "--Backtest:Approve=true", "--Backtest:OperationId=" + operation, "--Backtest:DefinitionJson=" + System.Text.Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(d)) }) start.ArgumentList.Add(arg);
            if (snapshot is not null) start.ArgumentList.Add("--Backtest:SnapshotId=" + snapshot);
            if (fingerprint is not null) start.ArgumentList.Add("--Backtest:ExpectedFingerprint=" + fingerprint);
            start.Environment["DOTNET_ENVIRONMENT"] = "Development"; start.Environment["ConnectionStrings__BetStats"] = fixture.GetConnectionString(); start.Environment["Ingestion__RawStoragePath"] = s.Root;
            using var child = System.Diagnostics.Process.Start(start)!; var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await child.WaitForExitAsync(timeout.Token); } catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
            Assert.Equal(0, child.ExitCode); Assert.Empty(await stderr);
            using var json = System.Text.Json.JsonDocument.Parse(await stdout); Assert.Equal(action, json.RootElement.GetProperty("Action").GetString()); return json.RootElement.GetProperty("Result").Clone();
        }
        _ = await Invoke("plan"); Assert.Empty(await s.Db.BacktestOperations.Where(e => e.OperationId == operation).ToArrayAsync());
        var result = await Invoke("run"); var id = result.GetProperty("SnapshotId").GetGuid(); var fp = result.GetProperty("Fingerprint").GetString()!;
        _ = await Invoke("inspect", id); Assert.True((await Invoke("verify-deep", id)).GetProperty("Reproducible").GetBoolean());
        Assert.Equal(id, (await Invoke("recover", fingerprint: fp)).GetProperty("SnapshotId").GetGuid());
    }
    [Theory] [InlineData("missing")] [InlineData("unbounded")] [InlineData("fingerprint")]
    public async Task Database_rejects_invalid_lease_and_fingerprint(string failure)
    {
        await using var s = await Create(); var d = await Definition(s); var operation = Guid.NewGuid(); var bytes = CanonicalDatasetJson.Serialize(d); var fp = CanonicalDatasetJson.Hash(bytes);
        s.Db.Add(new BacktestOperationEvent { Id = Guid.NewGuid(), OperationId = operation, Sequence = 1, Status = ResultOperationStatus.Requested,
            Fingerprint = fp, Request = bytes, OperatorId = "operator", Reason = "Validate DB contract" }); await s.Db.SaveChangesAsync();
        await using var db = fixture.CreateContext();
        db.Add(new BacktestOperationEvent { Id = Guid.NewGuid(), OperationId = operation, Sequence = 2, Status = ResultOperationStatus.Running,
            Fingerprint = failure == "fingerprint" ? new('a', 64) : fp, Request = bytes, OwnerToken = Guid.NewGuid(),
            LeaseUntilUtc = failure == "missing" ? null : (await s.Now()).AddMinutes(failure == "unbounded" ? 31 : 1), OperatorId = "operator", Reason = "Reject invalid lease/request" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(1, await s.Db.BacktestOperations.CountAsync(e => e.OperationId == operation));
    }
    [Fact] public async Task Revocation_between_assembly_and_publication_prevents_artifact_commit()
    {
        await using var s = await Create(); var d = await Definition(s); var plan = await s.Get<IHistoricalBacktests>().PlanAsync(d); var hash = CanonicalDatasetJson.Fingerprint(plan);
        await using var blocker = fixture.CreateContext(); await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({hash},9012))");
        using var scope = s.Provider.CreateScope(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var operation = Guid.NewGuid();
        var run = scope.ServiceProvider.GetRequiredService<IHistoricalBacktests>().RunAsync(new(operation, d, "operator", "Serialize concurrent authorization revocation", true), timeout.Token);
        var waiting = false;
        for (var i = 0; i < 200; i++)
        {
            waiting = await s.Db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory' AND query LIKE '%9012%'").SingleAsync() > 0;
            if (waiting) break; await Task.Delay(25);
        }
        Assert.True(waiting, "Operation must finish assembly and wait for publication content lock.");
        var policy = await s.Db.SourcePolicies.Include(p => p.Audit).Include(p => p.Permissions).SingleAsync(p => p.DataSourceId == s.Source);
        policy.Revoke(Guid.NewGuid(), "operator", "Revoke while publication is blocked", await s.Now()); await s.Db.SaveChangesAsync();
        await tx.CommitAsync(); var result = await run; Assert.Equal(ResultOperationStatus.Failed, result.Status); Assert.Equal("source_permission_denied", result.FailureCode);
        Assert.Null(result.SnapshotId); Assert.Empty(await s.Db.Backtests.Where(a => a.DatasetId == d.DatasetId).ToArrayAsync());
    }
}
