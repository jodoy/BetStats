using BetStats.Application.Datasets;
using BetStats.Application.Pipeline;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BetStats.Infrastructure.Pipeline;

public sealed class PostgreSqlPipeline(BetStatsDbContext db)
{
    private Task<DateTime> Now(CancellationToken token) => QualityPersistence.Now(db, token);
    private async Task<PipelineJob> LockJob(Guid id, CancellationToken token) =>
        (await db.Set<PipelineJob>().FromSqlInterpolated($"SELECT * FROM pipeline.\"Jobs\" WHERE \"Id\"={id} FOR UPDATE").ToListAsync(token)).Single();
    private async Task<PipelineExecution> LockExecution(Guid id, CancellationToken token) =>
        (await db.Set<PipelineExecution>().FromSqlInterpolated($"SELECT * FROM pipeline.\"Executions\" WHERE \"Id\"={id} FOR UPDATE").ToListAsync(token)).Single();
    private async Task<PipelineDefinition> Definition(PipelineExecution e, CancellationToken token) =>
        CanonicalDatasetJson.Deserialize<PipelineDefinition>((await db.Set<PipelineJobVersion>().AsNoTracking().SingleAsync(v => v.JobId == e.JobId && v.Version == e.DefinitionVersion, token)).Content);
    private void Receipt(PipelineJob job, PipelineExecution? execution, PipelineState state, string category, PipelineApproval approval, DateTime now, PipelineOutcome? outcome = null) =>
        db.Add(new PipelineReceipt { Id = Guid.NewGuid(), JobId = job.Id, ExecutionId = execution?.Id, Owner = execution?.Owner ?? Guid.Empty,
            State = state, Category = category, Actor = approval.Actor, Reason = approval.Reason, RecordedUtc = now, ArtifactId = outcome?.ArtifactId, ArtifactHash = outcome?.ArtifactHash });

    public async Task<object> PlanAsync(Guid id, PipelineDefinition definition, PipelineApproval approval, CancellationToken token = default)
    {
        approval.Validate(); definition.Validate(); if (id == Guid.Empty) throw new ArgumentException("Job UUID required.");
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({id.ToString()},9015))", token);
        var job = await db.Set<PipelineJob>().AsNoTracking().AnyAsync(x => x.Id == id, token) ? await LockJob(id, token) : null;
        if (job?.State == PipelineState.Running) throw new InvalidOperationException("Disable and finish the active execution before revising a job.");
        if (job is null) { job = new() { Id = id }; db.Add(job); }
        var content = CanonicalDatasetJson.Serialize(definition); var hash = CanonicalDatasetJson.Hash(content);
        if (content.Length > 2097152) throw new ArgumentException("Canonical job definition exceeds storage bound.");
        var prior = job.Version == 0 ? null : await db.Set<PipelineJobVersion>().SingleAsync(v => v.JobId == id && v.Version == job.Version, token);
        if (prior?.Hash == hash) { await tx.CommitAsync(token); return new { JobId = id, job.Version, Hash = hash, Reused = true }; }
        job.Version++; job.State = PipelineState.Disabled; job.Enabled = false; job.NextDueUtc = definition.Schedule.FirstDueUtc;
        var now = await Now(token);
        db.Add(new PipelineJobVersion { JobId = id, Version = job.Version, Hash = hash, Content = content, Actor = approval.Actor, Reason = approval.Reason, RecordedUtc = now });
        Receipt(job, null, job.State, "planned_disabled", approval, now);
        await db.SaveChangesAsync(token); await tx.CommitAsync(token);
        return new { JobId = id, job.Version, Hash = hash, Reused = false };
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, PipelineApproval approval, CancellationToken token = default)
    {
        approval.Validate(); db.ChangeTracker.Clear(); await using var tx = await db.Database.BeginTransactionAsync(token); var job = await LockJob(id, token);
        if (enabled && job.State is not (PipelineState.Disabled or PipelineState.Pending)) throw new InvalidOperationException("Explicit retry/recovery required for terminal or active jobs.");
        job.Enabled = enabled;
        if (job.State != PipelineState.Running) job.State = enabled ? PipelineState.Pending : PipelineState.Disabled;
        Receipt(job, null, job.State, enabled ? "enabled" : "disabled", approval, await Now(token));
        await db.SaveChangesAsync(token); await tx.CommitAsync(token);
    }

    public async Task<PipelineClaim?> AcquireAsync(PipelineApproval approval, CancellationToken token = default)
    {
        approval.Validate(); db.ChangeTracker.Clear(); await using var tx = await db.Database.BeginTransactionAsync(token);
        var jobs = await db.Set<PipelineJob>().FromSqlRaw("SELECT * FROM pipeline.\"Jobs\" WHERE \"Enabled\" AND \"State\"=1 AND \"NextDueUtc\"<=clock_timestamp() ORDER BY \"NextDueUtc\",\"Id\" LIMIT 1 FOR UPDATE SKIP LOCKED").ToListAsync(token);
        if (jobs.Count == 0) { await tx.CommitAsync(token); return null; }
        var job = jobs[0]; var definition = CanonicalDatasetJson.Deserialize<PipelineDefinition>((await db.Set<PipelineJobVersion>().SingleAsync(v => v.JobId == job.Id && v.Version == job.Version, token)).Content);
        definition.Validate(); var now = await Now(token);
        var e = new PipelineExecution { Id = Guid.NewGuid(), JobId = job.Id, DefinitionVersion = job.Version, Owner = Guid.NewGuid(), PlannedUtc = job.NextDueUtc!.Value,
            StartedUtc = now, LeaseUntilUtc = now.AddSeconds(definition.LeaseSeconds), Attempt = 1, State = PipelineState.Running };
        e.Fingerprint = CanonicalDatasetJson.Fingerprint(new { e.JobId, e.DefinitionVersion, e.PlannedUtc, Definition = definition.Fingerprint });
        job.State = PipelineState.Running; db.Add(e); Receipt(job, e, e.State, "acquired", approval, now);
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return Claim(e, definition);
    }
    private static PipelineClaim Claim(PipelineExecution e, PipelineDefinition d) => new(e.Id, e.JobId, e.DefinitionVersion, e.Owner, e.PlannedUtc, e.StartedUtc, e.LeaseUntilUtc, e.Attempt, d);

    public async Task CheckAsync(PipelineClaim claim, CancellationToken token = default)
    {
        var e = await db.Set<PipelineExecution>().AsNoTracking().SingleAsync(x => x.Id == claim.ExecutionId, token);
        if (e.Owner != claim.Owner || e.State != PipelineState.Running || e.LeaseUntilUtc <= await Now(token)) throw new InvalidOperationException("execution_fence_lost");
        if (e.CancelRequested) throw new OperationCanceledException("operator_cancelled");
    }

    // A separate session remains locked throughout the work, including child publication.
    // Recovery must acquire this same lock before changing the owner.
    public async Task<NpgsqlConnection> OwnAsync(Guid execution, CancellationToken token = default)
    {
        // Closing must physically release this session's advisory lock, without waiting
        // for a pooled connection's deferred reset on its next checkout.
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync(token);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@id,9015))", connection);
            command.Parameters.AddWithValue("id", execution.ToString());
            if ((bool)(await command.ExecuteScalarAsync(token))! != true) throw new InvalidOperationException("execution_live_owner");
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task CompleteAsync(PipelineClaim claim, PipelineOutcome outcome, PipelineApproval approval, CancellationToken token = default, byte[]? artifact = null,
        Func<CancellationToken, Task>? authorizePublication = null)
    {
        approval.Validate();
        if (outcome.State is not (PipelineState.Completed or PipelineState.Failed or PipelineState.Cancelled or PipelineState.Blocked) ||
            outcome.Category.Length is < 1 or > 100 || outcome.Category.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("Sanitized terminal outcome required.");
        db.ChangeTracker.Clear(); await using var tx = await db.Database.BeginTransactionAsync(token);
        var job = await LockJob(claim.JobId, token); var e = await LockExecution(claim.ExecutionId, token); var now = await Now(token);
        if (e.Owner != claim.Owner || e.State != PipelineState.Running || e.LeaseUntilUtc <= now) throw new InvalidOperationException("execution_fence_lost");
        var terminal = e.CancelRequested ? PipelineState.Cancelled : outcome.State;
        if (artifact is not null)
        {
            if (authorizePublication is not null) await authorizePublication(token);
            if (artifact.Length > 16777216 || outcome.ArtifactId != e.Id || outcome.ArtifactHash != CanonicalDatasetJson.Hash(artifact)) throw new InvalidDataException("Pipeline artifact binding mismatch.");
            var previous = await db.Set<PipelineArtifact>().SingleOrDefaultAsync(x => x.ExecutionId == e.Id, token);
            if (previous is not null && previous.Hash != outcome.ArtifactHash) throw new InvalidDataException("Immutable execution output conflict.");
            if (previous is null) db.Add(new PipelineArtifact { ExecutionId = e.Id, Content = artifact, Hash = outcome.ArtifactHash, RecordedUtc = now });
        }
        e.State = terminal; e.CompletedUtc = now; job.State = terminal;
        if (terminal == PipelineState.Completed)
        {
            job.LastSuccessUtc = now; job.NextDueUtc = claim.Definition.Schedule.Next(e.PlannedUtc, now);
            if (job.NextDueUtc is not null) job.State = job.Enabled ? PipelineState.Pending : PipelineState.Disabled;
        }
        Receipt(job, e, terminal, e.CancelRequested ? "operator_cancelled" : outcome.Category, approval, now, outcome);
        // Operational diagnostics are sanitized and bounded; immutable audit receipts are retained separately.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(9015,1)", token);
        var logId = Guid.NewGuid(); var duration = (now - e.StartedUtc).TotalMilliseconds;
        var category = e.CancelRequested ? "operator_cancelled" : outcome.Category;
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO pipeline.\"Diagnostics\" (\"Id\",\"ExecutionId\",\"JobId\",\"State\",\"Category\",\"DurationMilliseconds\") VALUES ({logId},{e.Id},{e.JobId},{(int)terminal},{category},{duration})", token);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM pipeline.\"Diagnostics\" WHERE \"RecordedUtc\"<clock_timestamp()-interval '30 days' OR \"Id\" IN (SELECT \"Id\" FROM pipeline.\"Diagnostics\" ORDER BY \"RecordedUtc\" DESC,\"Id\" OFFSET 1000)", token);
        await db.SaveChangesAsync(token); await tx.CommitAsync(token);
    }

    public async Task RenewAsync(PipelineClaim claim, CancellationToken token = default)
    {
        var seconds = claim.Definition.LeaseSeconds;
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE pipeline.\"Executions\" SET \"LeaseUntilUtc\"=clock_timestamp()+make_interval(secs=>{seconds}) WHERE \"Id\"={claim.ExecutionId} AND \"Owner\"={claim.Owner} AND \"State\"=2 AND NOT \"CancelRequested\" AND \"LeaseUntilUtc\">clock_timestamp()", token);
        if (changed != 1) throw new OperationCanceledException("execution_cancelled_or_fence_lost");
    }

    public async Task CancelAsync(Guid execution, PipelineApproval approval, CancellationToken token = default)
    {
        approval.Validate(); db.ChangeTracker.Clear(); var existing = await db.Set<PipelineExecution>().AsNoTracking().SingleAsync(x => x.Id == execution, token);
        await using var tx = await db.Database.BeginTransactionAsync(token); var job = await LockJob(existing.JobId, token); var e = await LockExecution(execution, token);
        if (e.State != PipelineState.Running) throw new InvalidOperationException("Only active executions can be cancelled.");
        if (!e.CancelRequested) { e.CancelRequested = true; Receipt(job, e, e.State, "cancellation_requested", approval, await Now(token)); await db.SaveChangesAsync(token); }
        await tx.CommitAsync(token);
    }

    public async Task<PipelineClaim> RetryAsync(Guid execution, bool recover, PipelineApproval approval, CancellationToken token = default)
    {
        approval.Validate(); db.ChangeTracker.Clear(); await using var owner = await OwnAsync(execution, token);
        var existing = await db.Set<PipelineExecution>().AsNoTracking().SingleAsync(x => x.Id == execution, token);
        await using var tx = await db.Database.BeginTransactionAsync(token); var job = await LockJob(existing.JobId, token); var e = await LockExecution(execution, token);
        var now = await Now(token); var definition = await Definition(e, token);
        if (await db.Set<PipelineArtifact>().AnyAsync(x => x.ExecutionId == execution, token)) throw new InvalidOperationException("Immutable output already exists; inspect the terminal receipt.");
        if (job.Version != e.DefinitionVersion || e.Attempt >= definition.MaximumAttempts ||
            (recover ? e.State != PipelineState.Running || e.LeaseUntilUtc > now : e.State is not (PipelineState.Failed or PipelineState.Blocked or PipelineState.Cancelled)))
            throw new InvalidOperationException("Explicit bounded recovery/retry is unavailable.");
        e.Owner = Guid.NewGuid(); e.Attempt++; e.State = PipelineState.Running; e.StartedUtc = now; e.LeaseUntilUtc = now.AddSeconds(definition.LeaseSeconds);
        e.CompletedUtc = null; e.CancelRequested = false; job.State = PipelineState.Running;
        Receipt(job, e, e.State, recover ? "recovered" : "retried", approval, now);
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return Claim(e, definition);
    }

    public async Task<object> StatusAsync(Guid? job, CancellationToken token = default)
    {
        var now = await Now(token);
        var executions = await db.Set<PipelineExecution>().AsNoTracking().Where(x => job == null || x.JobId == job).OrderByDescending(x => x.StartedUtc).Take(100).ToArrayAsync(token);
        return new { Infrastructure = "Available", ProviderDataReadiness = "NotCertified", DatabaseUtc = now,
            Definitions = await db.Set<PipelineJobVersion>().AsNoTracking().Where(x => job == null || x.JobId == job).OrderByDescending(x => x.RecordedUtc).Take(100)
                .Select(x => new { x.JobId, x.Version, x.Hash, x.RecordedUtc }).ToArrayAsync(token),
            Jobs = await db.Set<PipelineJob>().AsNoTracking().Where(x => job == null || x.Id == job).OrderBy(x => x.Id).Take(100).ToArrayAsync(token),
            Executions = executions.Select(x => new { x.Id, x.JobId, x.DefinitionVersion, x.Fingerprint, x.State, x.Owner, x.PlannedUtc, x.StartedUtc, x.LeaseUntilUtc, x.CompletedUtc, x.Attempt, x.CancelRequested,
                DurationMilliseconds = ((x.CompletedUtc ?? now) - x.StartedUtc).TotalMilliseconds,
                LocalImportOperationId = PipelineOperationIds.Child(x.Id, "local-import"), PredictionOperationId = PipelineOperationIds.Child(x.Id, "predictions"),
                FeatureOperationIds = Enumerable.Range(1, x.Attempt).Select(a => PipelineOperationIds.Child(x.Id, "features-" + a)).ToArray() }).ToArray(),
            Receipts = await db.Set<PipelineReceipt>().AsNoTracking().Where(x => job == null || x.JobId == job).OrderByDescending(x => x.RecordedUtc).Take(100).Select(x => new { x.Id, x.JobId, x.ExecutionId, x.Owner, x.State, x.Category, x.RecordedUtc, x.ArtifactId, x.ArtifactHash }).ToArrayAsync(token) };
    }
}
