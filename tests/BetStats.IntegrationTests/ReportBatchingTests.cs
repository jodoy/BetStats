using BetStats.Application.Datasets;
using BetStats.Application.Quality;
using BetStats.Domain.Quality;
using BetStats.Domain.Governance;
using BetStats.Application.Governance;
using BetStats.Application.Football;
using Microsoft.Extensions.DependencyInjection;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace BetStats.IntegrationTests;

public sealed class ReportBatchingTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class LockOrder(bool left, TaskCompletionSource<bool> leftLocked, TaskCompletionSource<bool> rightEntered) : DbCommandInterceptor
    {
        public bool Enabled;
        public Guid FirstSource;
        private int locks;
        private Guid first;
        private static bool SourceLock(DbCommand command) => command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal) && command.CommandText.Contains("\"DataSources\"", StringComparison.Ordinal);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken token = default)
        {
            if (Enabled && SourceLock(command) && Interlocked.Increment(ref locks) == 1)
            {
                first = (Guid)command.Parameters[0].Value!;
                // If both batches request the same first source, release the
                // left reader before the right reader waits on that row lock.
                if (!left && first == FirstSource) rightEntered.TrySetResult(true);
            }
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data, int result, CancellationToken token = default)
        {
            if (Enabled && SourceLock(command) && locks == 1)
            {
                if (left) { leftLocked.TrySetResult(true); await rightEntered.Task.WaitAsync(token); }
                else if (first != FirstSource) rightEntered.TrySetResult(true);
            }
            return result;
        }
    }
    [Fact]
    public async Task Reversed_snapshot_batches_use_one_global_source_lock_order()
    {
        var leftLocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rightEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var leftLocks = new LockOrder(true, leftLocked, rightEntered); var rightLocks = new LockOrder(false, leftLocked, rightEntered);
        await using var left = await new ResultOperationsWorkflowTests(fixture).Create(true, configure: services => services.AddDbContext<BetStats.Infrastructure.Persistence.BetStatsDbContext>(o => o.AddInterceptors(leftLocks)));
        await using var right = await new ResultOperationsWorkflowTests(fixture).Create(true, configure: services => services.AddDbContext<BetStats.Infrastructure.Persistence.BetStatsDbContext>(o => o.AddInterceptors(rightLocks)));
        async Task<Guid> Snapshot(ResultOperationsWorkflowTests.Scenario scenario)
        {
            var before = await scenario.Db.DatasetArtifacts.Select(a => a.Id).ToArrayAsync();
            _ = await BacktestWorkflowTests.Definition(scenario);
            return await scenario.Db.DatasetArtifacts.Where(a => !before.Contains(a.Id)).Select(a => a.Id).SingleAsync();
        }
        var leftId = await Snapshot(left); var rightId = await Snapshot(right);
        var ids = new[] { (left.Source, leftId), (right.Source, rightId) }.OrderBy(x => x.Source).Select(x => x.Item2).ToArray();
        leftLocks.FirstSource = rightLocks.FirstSource = new[] { left.Source, right.Source }.Order().First();
        leftLocks.Enabled = rightLocks.Enabled = true;
        using var leftScope = left.Provider.CreateScope(); using var rightScope = right.Provider.CreateScope();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var first = leftScope.ServiceProvider.GetRequiredService<IDatasets>().InspectManyAsync(ids, timeout.Token);
            await leftLocked.Task.WaitAsync(timeout.Token);
            var second = rightScope.ServiceProvider.GetRequiredService<IDatasets>().InspectManyAsync(ids.Reverse().ToArray(), timeout.Token);
            var reports = await Task.WhenAll(first, second);
            Assert.Equal(ids, reports[0].Select(s => s.Id)); Assert.Equal(ids.Reverse(), reports[1].Select(s => s.Id));
        }
        finally { await timeout.CancelAsync(); }
    }
    [Fact]
    public async Task Result_batch_preserves_individual_reports_and_observes_next_read_revocation()
    {
        await using var s = await new ResultOperationsWorkflowTests(fixture).Create(true);
        var query = await s.Query(); var results = s.Get<IFootballResults>();
        var events = await s.Db.FootballResults.AsNoTracking().Where(r => r.SourceId == s.Source).Select(r => r.EventId).Distinct().ToArrayAsync();
        var queries = events.Select(id => query with { EventId = id }).ToArray();
        var expected = new List<FootballResultReport>();
        foreach (var q in queries) expected.Add(await results.ReadAsync(q));
        var actual = await results.ReadManyAsync(queries);
        Assert.Equal(expected.Select(CanonicalDatasetJson.Fingerprint), actual.Select(CanonicalDatasetJson.Fingerprint));
        Assert.Empty(await results.ReadManyAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(() => results.ReadManyAsync(Enumerable.Repeat(query, 101).ToArray()));
        var policy = await s.Db.SourcePolicies.Include(p => p.Permissions).Include(p => p.Audit).SingleAsync(p => p.DataSourceId == s.Source);
        policy.Revoke(Guid.NewGuid(), "synthetic:operator", "Result batch revocation regression", await s.Now()); await s.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => results.ReadManyAsync(queries));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gate_batch_matches_single_queries_and_does_not_cache_revocation(bool reconstruction)
    {
        await using var s = await new QualityWorkflowTests(fixture).Create();
        await s.Import(); var now = await s.Now();
        var observations = await s.Db.Observations.AsNoTracking().Where(o => o.DataSourceId == s.Source && o.Type == ObservationType.EventDate).Select(o => o.Id).Take(20).ToArrayAsync();
        var queries = observations.Select(id => new EligibilityQuery(id, now, DataPurpose.InternalAnalytics, new UsageContext(),
            reconstruction ? DatasetMode.RetrospectiveReconstruction : DatasetMode.HistoricalAsKnown, reconstruction ? now : null)).ToArray();
        var expected = new List<QualityGateResult>();
        foreach (var q in queries) expected.Add(await s.Gate.EvaluateAsync(q));
        var actual = await s.Gate.EvaluateManyAsync(queries);
        Assert.Equal(expected.Select(CanonicalDatasetJson.Fingerprint), actual.Select(CanonicalDatasetJson.Fingerprint));
        var policy = await s.Db.SourcePolicies.Include(p => p.Permissions).Include(p => p.Audit).SingleAsync(p => p.DataSourceId == s.Source);
        policy.Revoke(Guid.NewGuid(), "synthetic:operator", "Batch read revocation regression", await s.Now()); await s.Db.SaveChangesAsync();
        var revoked = await s.Gate.EvaluateManyAsync(queries);
        Assert.All(revoked, result => { Assert.False(result.Eligible); Assert.Contains(result.Reasons, r => r.StartsWith("current_policy_", StringComparison.Ordinal)); });
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Quality_matches_legacy_for_zero_one_and_multiple_observation_matches(int matches)
    {
        await using var s = await new QualityWorkflowTests(fixture).Create();
        var imported = await s.Import();
        var rawId = await s.Raw(imported.RunId);
        var original = await s.Db.Observations.AsNoTracking().FirstAsync(o => o.RawPayloadId == rawId && o.Type == ObservationType.EventDate);
        var anchor = await s.Db.ProviderIdentities.AsNoTracking().SingleAsync(i => i.Id == original.ProviderIdentityId);
        if (matches == 0)
        {
            // Append an accepted report row whose reference has no observation.
            var assessment = await s.Db.QualityAssessments.AsNoTracking().FirstAsync(a => a.RawPayloadId == rawId && a.Row > 0);
            s.Db.QualityAssessments.Add(new() { Id = Guid.NewGuid(), ExecutionId = imported.AttemptId, DataSourceId = s.Source,
                RawPayloadId = rawId, Row = 900, RecordReference = "synthetic:no-observation", SportId = assessment.SportId,
                RuleId = assessment.RuleId, RuleVersion = assessment.RuleVersion, Passed = true, ReasonCode = "synthetic_pass", AssessedAtUtc = await s.Now() });
            await s.Db.SaveChangesAsync();
        }
        if (matches == 2)
        {
            var now = await s.Now();
            s.Db.Observations.Add(new Observation(Guid.NewGuid(), anchor,
                original.CanonicalId is { } id ? new CanonicalReference(anchor.EntityKind, id) : null,
                ObservationType.EventDate, now, now, now, rawPayloadId: rawId, corrects: original, dateValue: original.DateValue));
            await s.Db.SaveChangesAsync();
        }
        var before = await new LegacyQualityReports(s.Db, s.Gate).ReadAsync(imported.AttemptId);
        var after = await new QualityReports(s.Db, s.Gate).ReadAsync(imported.AttemptId);
        Assert.Equal(CanonicalDatasetJson.Fingerprint(before), CanonicalDatasetJson.Fingerprint(after));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1000)]
    public async Task Quality_matches_legacy_across_batch_boundaries(int size)
    {
        await using var s = await new QualityWorkflowTests(fixture).Create();
        var imported = await s.Import(); var rawId = await s.Raw(imported.RunId);
        var observation = await s.Db.Observations.AsNoTracking().FirstAsync(o => o.RawPayloadId == rawId && o.Type == ObservationType.EventDate);
        var reference = await s.Db.ProviderIdentities.Where(i => i.Id == observation.ProviderIdentityId).Select(i => i.ExternalId).SingleAsync();
        var execution = Guid.NewGuid(); var now = await s.Now();
        // The batch lookup contract does not need 1000 full imports. Reuse valid
        // provenance and seed distinct report rows; real gate behavior is covered
        // by the workflow suite and the zero/one/multiple tests above.
        for (var row = 1; row <= size; row++)
            s.Db.QualityAssessments.Add(new() { Id = Guid.NewGuid(), ExecutionId = execution, DataSourceId = s.Source,
                RawPayloadId = rawId, Row = row, RecordReference = reference, SportId = FootballQualityRules.Football,
                RuleId = "batch_lookup", RuleVersion = 1, Passed = true, ReasonCode = "synthetic_pass", AssessedAtUtc = now });
        await s.Db.SaveChangesAsync();
        var oldGate = new RecordingGate(); var newGate = new RecordingGate();
        var before = await new LegacyQualityReports(s.Db, oldGate).ReadAsync(execution);
        var after = await new QualityReports(s.Db, newGate).ReadAsync(execution);
        Assert.Equal(size, after.Total);
        Assert.Equal(oldGate.Ids, newGate.Ids);
        Assert.Equal(CanonicalDatasetJson.Fingerprint(before), CanonicalDatasetJson.Fingerprint(after));
    }
    private sealed class RecordingGate : IAnalyticalQualityGate
    {
        public List<Guid> Ids { get; } = [];
        public Task<QualityGateResult> EvaluateAsync(EligibilityQuery q, CancellationToken token = default)
        {
            Ids.Add(q.ObservationId);
            return Task.FromResult(new QualityGateResult(true, q.Mode, q.AsOfUtc, q.ReconstructionAtUtc, q.ObservationId, null, null, null, null, [], []));
        }
    }
}
