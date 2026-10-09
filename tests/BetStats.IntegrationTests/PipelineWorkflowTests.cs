using System.Text.Json;
using BetStats.Application.Coverage;
using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Football;
using BetStats.Application.Pipeline;
using BetStats.Application.Quality;
using BetStats.Domain.Coverage;
using BetStats.Domain.Identity;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BetStats.IntegrationTests;

public sealed class PipelineWorkflowTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly PipelineApproval Approval = new("test:operator", "Explicit synthetic pipeline workflow", true);
    private static async Task<int> Command(ResultOperationsWorkflowTests.Scenario s, string action, Guid? execution = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Pipeline:Action"] = action, ["Pipeline:Actor"] = Approval.Actor, ["Pipeline:Reason"] = Approval.Reason,
            ["Pipeline:Approve"] = "true", ["Pipeline:ExecutionId"] = execution?.ToString() }).Build();
        return await PipelineOperatorCommand.RunAsync(s.Scope.ServiceProvider, config, true);
    }
    private async Task<ResultOperationsWorkflowTests.Scenario> Create() => await new ResultOperationsWorkflowTests(fixture).Create();
    private static async Task<Guid> Local(ResultOperationsWorkflowTests.Scenario s, string file)
    {
        var plan = await s.Get<FootballImportOperations>().PlanAsync(s.Source, file, s.Fixture.Scope);
        var job = Guid.NewGuid(); var jobs = s.Get<PostgreSqlPipeline>();
        await jobs.PlanAsync(job, new(1, PipelineKind.LocalSynchronization, new(await s.Now()), JsonSerializer.Serialize(new LocalSynchronizationInput(plan, file))), Approval);
        await jobs.SetEnabledAsync(job, true, Approval); return job;
    }
    [Fact] public async Task Approved_local_sync_is_idempotent_and_missing_input_can_be_explicitly_retried()
    {
        await using var s = await Create(); var file = Path.Combine(s.Root, "pipeline-fiction.csv"); await File.WriteAllTextAsync(file, s.Fixture.Csv);
        var job = await Local(s, file); File.Delete(file);
        Assert.Equal(1, await Command(s, "run-once"));
        var execution = await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(x => x.JobId == job);
        Assert.Equal(PipelineState.Failed, execution.State); Assert.Empty(await s.Db.Set<PipelineArtifact>().Where(x => x.ExecutionId == execution.Id).ToArrayAsync());
        await File.WriteAllTextAsync(file, s.Fixture.Csv);
        Assert.Equal(1, await Command(s, "retry", execution.Id)); // BS-013 requires a human event review, even for prior synthetic mappings.
        var identities = await s.Db.ProviderIdentities.Where(i => i.DataSourceId == s.Source && i.EntityKind == CanonicalEntityKind.SportingEvent).ToArrayAsync();
        foreach (var identity in identities)
        {
            var decision = await s.Db.IdentityResolutions.Where(d => d.ProviderIdentityId == identity.Id).OrderByDescending(d => d.Version).FirstAsync();
            var reviewed = await s.Get<IIdentityReview>().DecideAsync(new(identity.Id, s.Source, ReviewAction.Approve,
                new(CanonicalEntityKind.SportingEvent, decision.CanonicalSportingEventId!.Value), decision.Version, "test:reviewer", "Explicitly review synthetic event before governed local recovery"));
            Assert.Equal("accepted", reviewed.Result);
        }
        Assert.Equal(0, await Command(s, "retry", execution.Id));
        var artifact = await s.Db.Set<PipelineArtifact>().AsNoTracking().SingleAsync(x => x.ExecutionId == execution.Id);
        Assert.Equal(artifact.Hash, CanonicalDatasetJson.Hash(artifact.Content));
        Assert.Equal(0, await Command(s, "run-once")); // No due work remains.
        Assert.Equal(1, await s.Db.Set<PipelineArtifact>().CountAsync(x => x.ExecutionId == execution.Id));
        Assert.Equal(1, await s.Db.Set<PipelineReceipt>().CountAsync(x => x.ExecutionId == execution.Id && x.State == PipelineState.Completed));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Changed_local_bytes_or_revoked_authorization_cannot_publish(bool revoke)
    {
        await using var s = await Create(); var file = Path.Combine(s.Root, "pipeline-fiction.csv"); await File.WriteAllTextAsync(file, s.Fixture.Csv);
        var job = await Local(s, file);
        if (revoke)
        {
            s.Db.ChangeTracker.Clear(); var policy = await s.Db.SourcePolicies.Include(p => p.Audit).SingleAsync(p => p.DataSourceId == s.Source);
            policy.Revoke(Guid.NewGuid(), "test:reviewer", "Withdraw fictional source authorization", await s.Now()); await s.Db.SaveChangesAsync();
        }
        else await File.AppendAllTextAsync(file, "\n");
        Assert.Equal(1, await Command(s, "run-once"));
        var execution = await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(x => x.JobId == job);
        Assert.NotEqual(PipelineState.Completed, execution.State); Assert.Empty(await s.Db.Set<PipelineArtifact>().Where(x => x.ExecutionId == execution.Id).ToArrayAsync());
        if (revoke)
        {
            Assert.Equal(1, await Command(s, "retry", execution.Id));
            Assert.Equal(PipelineState.Blocked, (await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(x => x.Id == execution.Id)).State);
            Assert.Empty(await s.Db.Set<PipelineArtifact>().Where(x => x.ExecutionId == execution.Id).ToArrayAsync());
        }
    }
    [Fact] public async Task Prematch_uses_actual_db_cutoff_and_frozen_postmatch_replay_preserves_every_prediction_byte()
    {
        await using var s = await Create();
        var request = await s.Request(); var target = await s.Db.Observations.SingleAsync(o => o.Id == request.Metadata.Definition.Targets[0].DateObservationId);
        var identity = await s.Db.ProviderIdentities.SingleAsync(i => i.Id == target.ProviderIdentityId);
        var value = new EventTimeValue(s.Fixture.TargetDate, new(12, 0), null, 0, null, EventTimePrecision.Minute);
        var raw = await s.Raw(new EventTimeSourceClaim(1, target.RawPayloadId!.Value, identity.ExternalId, new(s.Fixture.Scope.CompetitionReference, s.Fixture.Scope.SeasonReference), value));
        await s.Get<IHistoricalCoverage>().RecordTimeAsync(new(target.Id, raw.Id, value, "synthetic-source-bound-kickoff", null, null, raw.RetrievedAtUtc, Approval.Actor, Approval.Reason));
        var definition = await BacktestWorkflowTests.Definition(s, true);
        var now = await s.Now(); var planned = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddSeconds(-2);
        var kickoff = s.Fixture.TargetDate.ToDateTime(new(12, 0), DateTimeKind.Utc);
        var horizon = checked((int)(kickoff - planned).TotalSeconds);
        var jobs = s.Get<PostgreSqlPipeline>(); var job = Guid.NewGuid();
        await jobs.PlanAsync(job, new(1, PipelineKind.PrematchPrediction, new(planned), JsonSerializer.Serialize(new PrematchInput(request, definition)), PredictionHorizonSeconds: horizon), Approval);
        await jobs.SetEnabledAsync(job, true, Approval); Assert.Equal(0, await Command(s, "run-once"));
        var execution = await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(x => x.JobId == job);
        var artifact = await s.Db.Set<PipelineArtifact>().AsNoTracking().SingleAsync(x => x.ExecutionId == execution.Id);
        using var document = JsonDocument.Parse(artifact.Content);
        Assert.True(document.RootElement.GetProperty("LateExecution").GetBoolean()); Assert.True(execution.StartedUtc > execution.PlannedUtc);
        var published = document.RootElement.GetProperty("Result").GetProperty("published").GetProperty("SnapshotId").GetGuid();
        var frozen = await s.Get<IHistoricalBacktests>().InspectAsync(published);
        Assert.All(frozen.Manifest.Predictions, p => Assert.Equal(execution.StartedUtc, p.PredictionCutoffUtc));
        var before = CanonicalDatasetJson.Serialize(frozen.Manifest.Predictions);
        var later = await s.Get<IHistoricalBacktests>().PlanAsync(frozen.Manifest.Definition with { EvaluationCutoffUtc = await s.Now() });
        var features = await s.Get<IFootballResultDatasets>().InspectAsync(frozen.Manifest.Definition.DatasetId);
        var evaluated = new BacktestExecutor(new FrozenPredictionProvider(frozen.Manifest)).Execute(later.Definition, features, later.EvaluationEvidence);
        Assert.Equal(before, CanonicalDatasetJson.Serialize(evaluated.Predictions));
        Assert.All(evaluated.Report.Targets, t => { Assert.Equal(0, t.Eligible); Assert.All(t.Metrics, m => Assert.Null(m.Value)); });
        var evaluationJob = Guid.NewGuid();
        await jobs.PlanAsync(evaluationJob, new(1, PipelineKind.PostmatchEvaluation, new(await s.Now()), JsonSerializer.Serialize(new PostmatchInput(execution.Id, frozen.Id, frozen.Hash))), Approval);
        await jobs.SetEnabledAsync(evaluationJob, true, Approval); Assert.Equal(1, await Command(s, "run-once")); // Kickoff is genuinely in the future; no backdating.
        var blocked = await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(x => x.JobId == evaluationJob);
        Assert.Equal(PipelineState.Blocked, blocked.State);
        Assert.Equal(before, CanonicalDatasetJson.Serialize((await s.Get<IHistoricalBacktests>().InspectAsync(frozen.Id)).Manifest.Predictions));
    }
    [Fact] public async Task Operator_interface_remains_development_only_and_requires_approval()
    {
        await using var s = await Create();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Pipeline:Action"] = "run-once", ["Pipeline:Actor"] = "audit:claim", ["Pipeline:Reason"] = "Explicit verification" }).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PipelineOperatorCommand.RunAsync(s.Scope.ServiceProvider, config, false));
        await Assert.ThrowsAsync<ArgumentException>(() => PipelineOperatorCommand.RunAsync(s.Scope.ServiceProvider, config, true));
    }
    [Fact] public async Task Missing_precise_kickoff_excludes_prematch_without_publishing_predictions()
    {
        await using var s = await Create(); var request = await s.Request(); var definition = await BacktestWorkflowTests.Definition(s, true);
        var previousPredictions = await s.Db.Backtests.CountAsync();
        var jobs = s.Get<PostgreSqlPipeline>(); var job = Guid.NewGuid();
        await jobs.PlanAsync(job, new(1, PipelineKind.PrematchPrediction, new(await s.Now()), JsonSerializer.Serialize(new PrematchInput(request, definition))), Approval);
        await jobs.SetEnabledAsync(job, true, Approval); Assert.Equal(1, await Command(s, "run-once"));
        var execution = await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(x => x.JobId == job);
        Assert.Equal(PipelineState.Blocked, execution.State);
        Assert.Equal(previousPredictions, await s.Db.Backtests.CountAsync());
        Assert.Empty(await s.Db.Set<PipelineArtifact>().Where(x => x.ExecutionId == execution.Id).ToArrayAsync());
    }
    [Fact] public async Task Explicit_bounded_worker_polls_without_inventing_executions_for_an_empty_queue()
    {
        await using var s = await Create(); var before = await s.Db.Set<PipelineExecution>().CountAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Pipeline:Action"] = "work", ["Pipeline:Actor"] = Approval.Actor, ["Pipeline:Reason"] = Approval.Reason,
            ["Pipeline:Approve"] = "true", ["Pipeline:DurationSeconds"] = "1", ["Pipeline:MaximumExecutions"] = "1" }).Build();
        Assert.Equal(0, await PipelineOperatorCommand.RunAsync(s.Scope.ServiceProvider, config, true));
        Assert.Equal(before, await s.Db.Set<PipelineExecution>().CountAsync());
    }
    [Fact] public async Task Real_clock_same_day_prematch_to_postmatch_never_backdates_or_invents_results()
    {
        await using var clock = fixture.CreateContext();
        var now = await clock.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        var kickoff = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddSeconds(120);
        await using var s = await new ResultOperationsWorkflowTests(fixture).Create(targetKickoff: kickoff);
        var request = await s.Request(); var target = await s.Db.Observations.SingleAsync(o => o.Id == request.Metadata.Definition.Targets[0].DateObservationId);
        var identity = await s.Db.ProviderIdentities.SingleAsync(i => i.Id == target.ProviderIdentityId);
        var value = new EventTimeValue(s.Fixture.TargetDate, TimeOnly.FromDateTime(kickoff), null, 0, null, EventTimePrecision.Second);
        var raw = await s.Raw(new EventTimeSourceClaim(1, target.RawPayloadId!.Value, identity.ExternalId, new(s.Fixture.Scope.CompetitionReference, s.Fixture.Scope.SeasonReference), value));
        await s.Get<IHistoricalCoverage>().RecordTimeAsync(new(target.Id, raw.Id, value, "synthetic-live-clock-kickoff", null, null, raw.RetrievedAtUtc, Approval.Actor, Approval.Reason));
        var definition = await BacktestWorkflowTests.Definition(s, true, PredictionTimeBoundary.SourceBoundKickoffV1);
        definition = definition with { Evaluations = definition.Evaluations.Select(e => e with { CutoffPolicy = PredictionCutoffPolicy.BeforeJustifiedKickoff, PredictionHorizon = TimeSpan.FromSeconds(1) }).ToArray() };
        var jobs = s.Get<PostgreSqlPipeline>(); var job = Guid.NewGuid(); var planned = kickoff.AddSeconds(-60);
        await jobs.PlanAsync(job, new(1, PipelineKind.PrematchPrediction, new(planned), JsonSerializer.Serialize(new PrematchInput(request, definition)), LeaseSeconds: 20, PredictionHorizonSeconds: 60), Approval);
        await jobs.SetEnabledAsync(job, true, Approval);
        while (await s.Now() < planned) await Task.Delay(250);
        var originalClaim = (await jobs.AcquireAsync(Approval))!;
        await using (var owner = await jobs.OwnAsync(originalClaim.ExecutionId))
        {
            using var stop = new CancellationTokenSource();
            var heartbeat = Task.Run(async () =>
            {
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                    while (await timer.WaitForNextTickAsync(stop.Token))
                    {
                        await using var renewal = fixture.CreateContext();
                        await new PostgreSqlPipeline(renewal).RenewAsync(originalClaim, stop.Token);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            });
            try
            {
                var prepared = await s.Get<PipelineWork>().ExecuteAsync(originalClaim, Approval, default);
                Assert.Equal(PipelineState.Completed, prepared.Outcome.State);
                await Assert.ThrowsAsync<IOException>(() => jobs.CompleteAsync(originalClaim, prepared.Outcome, Approval,
                    artifact: prepared.Artifact, authorizePublication: _ => throw new IOException("synthetic_crash_before_parent_publication")));
            }
            finally { stop.Cancel(); await heartbeat; }
        }
        await s.Db.Database.ExecuteSqlRawAsync("SELECT pg_sleep(20.1)"); // Expire the real lease; never rewrite a clock.
        Assert.Equal(0, await Command(s, "recover", originalClaim.ExecutionId));
        var execution = await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(x => x.JobId == job);
        var artifact = await s.Db.Set<PipelineArtifact>().AsNoTracking().SingleAsync(x => x.ExecutionId == execution.Id);
        using var document = JsonDocument.Parse(artifact.Content);
        var id = document.RootElement.GetProperty("Result").GetProperty("published").GetProperty("SnapshotId").GetGuid();
        var frozen = await s.Get<IHistoricalBacktests>().InspectAsync(id);
        Assert.True(frozen.RecordedAtUtc < kickoff); Assert.True(execution.StartedUtc >= planned);
        Assert.Equal(2, execution.Attempt);
        Assert.All(frozen.Manifest.Predictions, p => Assert.Equal(originalClaim.StartedUtc, p.PredictionCutoffUtc));
        var childOperation = document.RootElement.GetProperty("Result").GetProperty("published").GetProperty("OperationId").GetGuid();
        Assert.Equal(1, await s.Db.BacktestOperations.CountAsync(x => x.OperationId == childOperation && x.Status == ResultOperationStatus.Succeeded));
        var original = CanonicalDatasetJson.Serialize(frozen.Manifest.Predictions);
        while (await s.Now() <= kickoff) await Task.Delay(250);
        var postmatch = Guid.NewGuid();
        await jobs.PlanAsync(postmatch, new(1, PipelineKind.PostmatchEvaluation, new(await s.Now()), JsonSerializer.Serialize(new PostmatchInput(execution.Id, frozen.Id, frozen.Hash))), Approval);
        await jobs.SetEnabledAsync(postmatch, true, Approval); Assert.Equal(0, await Command(s, "run-once"));
        var evaluatedExecution = await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(x => x.JobId == postmatch);
        Assert.True(evaluatedExecution.StartedUtc > kickoff); Assert.Equal(PipelineState.Completed, evaluatedExecution.State);
        var output = await s.Db.Set<PipelineArtifact>().AsNoTracking().SingleAsync(x => x.ExecutionId == evaluatedExecution.Id);
        using var evaluated = JsonDocument.Parse(output.Content);
        var report = CanonicalDatasetJson.Deserialize<BacktestManifest>(System.Text.Encoding.UTF8.GetBytes(evaluated.RootElement.GetProperty("Result").GetProperty("Report").GetRawText()));
        Assert.Equal(original, CanonicalDatasetJson.Serialize(report.Predictions));
        Assert.All(report.Report.Targets, t => { Assert.Equal(0, t.Eligible); Assert.All(t.Metrics, m => Assert.Null(m.Value)); });
        Assert.All(report.Samples, sample => Assert.Contains("eligible_outcome_missing_or_conflicting", sample.Reasons));
    }
}
