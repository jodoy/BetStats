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

namespace BetStats.IntegrationTests;

public sealed class ReportBatchingTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
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
