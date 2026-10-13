using System.Net;
using System.Text;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Infrastructure.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace BetStats.IntegrationTests;

public sealed class ApiFootballRawTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class Handler(string original) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(original, Encoding.UTF8, "application/json") });
    }
    [Theory] [InlineData("{malformed-fictional-json")] [InlineData("{\"synthetic\":true}")]
    public async Task Existing_raw_first_ingestion_preserves_provider_json_but_never_publishes_it_as_csv(string original)
    {
        await using var s = await new ResultOperationsWorkflowTests(fixture).Create();
        var policy = await s.Db.SourcePolicies.AsNoTracking().SingleAsync(p => p.DataSourceId == s.Source);
        var originalObservations = await s.Db.Observations.AsNoTracking().Where(o => o.DataSourceId == s.Source).Select(o => o.Id).OrderBy(id => id).ToArrayAsync();
        var originalResults = await s.Db.FootballResults.AsNoTracking().Where(o => o.SourceId == s.Source).Select(o => o.Id).OrderBy(id => id).ToArrayAsync();
        var scope = new FootballImportScope("999999", "2026");
        var context = new AcquisitionPageContext(1, s.Source, policy.Id, policy.Version, scope, AcquisitionCategory.PrematchMetadata);
        using var invoker = new HttpMessageInvoker(new Handler(original));
        var adapter = new ApiFootballFixtureAdapter(invoker, "fictional-test-key", true, () => true, context,
            new(1, 999999, 2026, new(2026, 10, 13), new(2026, 10, 19)), s.Get<ISourcePolicyEvaluator>(), TimeProvider.System);
        var before = await s.Now();
        var result = await s.Get<FootballIngestion>().RunAsync(adapter, scope, s.Get<RequestBudget>());
        Assert.NotEqual(ImportOutcome.Succeeded, result.Outcome); Assert.Equal(1, result.RetrievedPayloads);
        var raw = await s.Db.RawPayloads.AsNoTracking().SingleAsync(r => r.DataSourceId == s.Source && r.ExternalReference == "fixture:" + ApiFootballFixtureAdapter.Profile);
        var bytes = await s.Get<IRawPayloadStore>().ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, raw.ByteLength!.Value));
        Assert.Equal(original, Encoding.UTF8.GetString(bytes.Span)); Assert.True(raw.RecordedAtUtc >= before);
        Assert.Equal("application/json", raw.ContentType);
        var rawContext = await s.Db.FootballRawContexts.AsNoTracking().SingleAsync(c => c.RawId == raw.Id);
        Assert.Equal(scope.CompetitionReference, rawContext.CompetitionReference); Assert.Equal(scope.SeasonReference, rawContext.SeasonReference);
        Assert.Equal(originalObservations, await s.Db.Observations.AsNoTracking().Where(o => o.DataSourceId == s.Source).Select(o => o.Id).OrderBy(id => id).ToArrayAsync());
        Assert.Equal(originalResults, await s.Db.FootballResults.AsNoTracking().Where(o => o.SourceId == s.Source).Select(o => o.Id).OrderBy(id => id).ToArrayAsync());
        Assert.Equal(2, await s.Db.IngestionAuditEvents.CountAsync(a => a.AttemptId == result.AttemptId));
    }
}
