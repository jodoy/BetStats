using BetStats.Application.Datasets;
using BetStats.Application.Pipeline;
using BetStats.Infrastructure.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace BetStats.IntegrationTests;

public sealed class PipelineSchedulerTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly PipelineApproval Approval = new("test:operator", "Explicit fictional pipeline verification", true);
    private async Task<PipelineClaim> Start(int lease = 300)
    {
        await using var db = fixture.CreateContext(); var jobs = new PostgreSqlPipeline(db);
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        var id = Guid.NewGuid(); await jobs.PlanAsync(id, new(1, PipelineKind.LocalSynchronization, new(now), "{}", LeaseSeconds: lease), Approval);
        Assert.Null(await jobs.AcquireAsync(Approval));
        await jobs.SetEnabledAsync(id, true, Approval);
        return (await jobs.AcquireAsync(Approval))!;
    }
    [Fact] public async Task Acquisition_uses_database_due_clock_and_two_workers_claim_once()
    {
        await using var db = fixture.CreateContext(); var jobs = new PostgreSqlPipeline(db); var id = Guid.NewGuid();
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        await jobs.PlanAsync(id, new(1, PipelineKind.LocalSynchronization, new(now.AddSeconds(1)), "{}"), Approval);
        await jobs.SetEnabledAsync(id, true, Approval); Assert.Null(await jobs.AcquireAsync(Approval));
        await db.Database.ExecuteSqlRawAsync("SELECT pg_sleep(1.1)");
        await using var left = fixture.CreateContext(); await using var right = fixture.CreateContext();
        var acquired = await Task.WhenAll(new PostgreSqlPipeline(left).AcquireAsync(Approval), new PostgreSqlPipeline(right).AcquireAsync(Approval));
        var claim = Assert.Single(acquired, c => c is not null)!;
        Assert.True(claim.StartedUtc >= claim.PlannedUtc); Assert.True(claim.LeaseUntilUtc > claim.StartedUtc);
        await jobs.CompleteAsync(claim, new(PipelineState.Completed, "completed"), Approval);
        Assert.Single(await db.Set<PipelineExecution>().Where(x => x.JobId == id).ToArrayAsync());
        Assert.Equal(1, await db.Set<PipelineReceipt>().CountAsync(x => x.ExecutionId == claim.ExecutionId && x.State == PipelineState.Completed));
    }
    [Fact] public async Task Live_session_cannot_be_taken_over_even_after_lease_expires()
    {
        var claim = await Start(1); await using var db = fixture.CreateContext(); var jobs = new PostgreSqlPipeline(db);
        await using (var owner = await jobs.OwnAsync(claim.ExecutionId))
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_sleep(1.1)");
            await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.RetryAsync(claim.ExecutionId, true, Approval));
            await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.CompleteAsync(claim, new(PipelineState.Completed, "completed"), Approval));
        }
        var recovered = await jobs.RetryAsync(claim.ExecutionId, true, Approval);
        Assert.NotEqual(claim.Owner, recovered.Owner); Assert.Equal(2, recovered.Attempt); Assert.Equal(claim.PlannedUtc, recovered.PlannedUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.CompleteAsync(claim, new(PipelineState.Completed, "completed"), Approval));
        await jobs.CompleteAsync(recovered, new(PipelineState.Completed, "completed"), Approval);
    }
    [Fact] public async Task Cancellation_is_durable_and_retry_is_explicit_and_bounded()
    {
        var claim = await Start(); await using var db = fixture.CreateContext(); var jobs = new PostgreSqlPipeline(db);
        await jobs.CancelAsync(claim.ExecutionId, Approval);
        await Assert.ThrowsAsync<OperationCanceledException>(() => jobs.CheckAsync(claim));
        var actual = await jobs.CompleteAsync(claim, new(PipelineState.Completed, "completed"), Approval);
        Assert.Equal(PipelineState.Cancelled, actual.State); Assert.Equal("operator_cancelled", actual.Category);
        Assert.Equal(PipelineState.Cancelled, (await db.Set<PipelineExecution>().AsNoTracking().SingleAsync(x => x.Id == claim.ExecutionId)).State);
        for (var attempt = 2; attempt <= 3; attempt++)
        {
            claim = await jobs.RetryAsync(claim.ExecutionId, false, Approval); Assert.Equal(attempt, claim.Attempt);
            await jobs.CompleteAsync(claim, new(PipelineState.Failed, "local_input_unavailable"), Approval);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.RetryAsync(claim.ExecutionId, false, Approval));
    }
    [Fact] public async Task Publication_is_fenced_and_audit_and_artifacts_are_immutable()
    {
        var claim = await Start(); await using var db = fixture.CreateContext(); var jobs = new PostgreSqlPipeline(db);
        var bytes = CanonicalDatasetJson.Serialize(new { Version = 1, claim.PlannedUtc, ActualCutoffUtc = claim.StartedUtc });
        await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.CheckAsync(claim with { StartedUtc = claim.StartedUtc.AddTicks(-10) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.CheckAsync(claim with { Definition = claim.Definition with { MaximumAttempts = 2 } }));
        var outcome = new PipelineOutcome(PipelineState.Completed, "completed", claim.ExecutionId, CanonicalDatasetJson.Hash(bytes));
        await jobs.CompleteAsync(claim, outcome, Approval, artifact: bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.CompleteAsync(claim, outcome, Approval, artifact: bytes));
        Assert.Single(await db.Set<PipelineArtifact>().Where(x => x.ExecutionId == claim.ExecutionId).ToArrayAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE pipeline.\"Artifacts\" SET \"Hash\"='rewrite' WHERE \"ExecutionId\"={claim.ExecutionId}"));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM pipeline.\"Receipts\" WHERE \"ExecutionId\"={claim.ExecutionId}"));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE pipeline.\"Executions\" SET \"PlannedUtc\"=clock_timestamp() WHERE \"Id\"={claim.ExecutionId}"));
    }
    [Fact] public async Task Replanning_versions_is_explicit_disabled_and_read_commands_do_not_write()
    {
        await using var db = fixture.CreateContext(); var jobs = new PostgreSqlPipeline(db); var id = Guid.NewGuid();
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        var definition = new PipelineDefinition(1, PipelineKind.LocalSynchronization, new(now), "{}");
        await jobs.PlanAsync(id, definition, Approval); await jobs.PlanAsync(id, definition, Approval);
        Assert.Equal(1, await db.Set<PipelineJobVersion>().CountAsync(x => x.JobId == id));
        await jobs.PlanAsync(id, definition with { MaximumAttempts = 2 }, Approval);
        Assert.Equal(2, await db.Set<PipelineJobVersion>().CountAsync(x => x.JobId == id));
        var count = await db.Set<PipelineReceipt>().CountAsync(); _ = await jobs.StatusAsync(id);
        Assert.Equal(count, await db.Set<PipelineReceipt>().CountAsync()); Assert.Null(await jobs.AcquireAsync(Approval));
        await Assert.ThrowsAsync<ArgumentException>(() => jobs.SetEnabledAsync(id, true, Approval with { Approved = false }));
    }
    [Fact] public async Task Database_connection_outage_never_fabricates_a_terminal_receipt()
    {
        var claim = await Start(); await using var db = fixture.CreateContext(); await db.Database.OpenConnectionAsync();
        var backend = ((Npgsql.NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
        await using var killer = fixture.CreateContext();
        await killer.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_terminate_backend({backend})");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgreSqlPipeline(db).CompleteAsync(claim, new(PipelineState.Completed, "completed"), Approval));
        Assert.IsAssignableFrom<Npgsql.NpgsqlException>(failure.InnerException);
        Assert.Equal(PipelineState.Running, (await killer.Set<PipelineExecution>().AsNoTracking().SingleAsync(x => x.Id == claim.ExecutionId)).State);
        Assert.Empty(await killer.Set<PipelineReceipt>().Where(x => x.ExecutionId == claim.ExecutionId && x.State == PipelineState.Completed).ToArrayAsync());
        await new PostgreSqlPipeline(killer).CompleteAsync(claim, new(PipelineState.Failed, "database_unavailable"), Approval);
    }
    [Fact] public async Task Operational_diagnostics_are_bounded_without_pruning_audit_receipts()
    {
        var claim = await Start(); await using var db = fixture.CreateContext(); var jobs = new PostgreSqlPipeline(db);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO pipeline.\"Diagnostics\" (\"Id\",\"ExecutionId\",\"JobId\",\"State\",\"Category\",\"DurationMilliseconds\") SELECT gen_random_uuid(),{claim.ExecutionId},{claim.JobId},2,'synthetic_retention_probe',0 FROM generate_series(1,1002)");
        var before = await db.Set<PipelineReceipt>().CountAsync(x => x.JobId == claim.JobId);
        await jobs.CompleteAsync(claim, new(PipelineState.Completed, "completed"), Approval);
        Assert.Equal(1000, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pipeline.\"Diagnostics\"").SingleAsync());
        Assert.Equal(before + 1, await db.Set<PipelineReceipt>().CountAsync(x => x.JobId == claim.JobId));
    }
}
