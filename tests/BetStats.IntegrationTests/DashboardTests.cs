using BetStats.Application.Dashboard;
using BetStats.Application.Evaluation;
using BetStats.Domain.Governance;
using BetStats.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;

namespace BetStats.IntegrationTests;

public sealed class DashboardTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class Loopback : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        { app.Use((context, following) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; return following(); }); next(app); };
    }
    private sealed class Host(string connection, string rawRoot, bool demo = false, string environment = "Development") : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment(environment)
            .UseSetting("ConnectionStrings:BetStats", connection).UseSetting("Ingestion:RawStoragePath", rawRoot)
            .UseSetting("Dashboard:DemoEnabled", demo.ToString()).ConfigureServices(services => services.AddSingleton<IStartupFilter, Loopback>());
    }
    [Fact]
    public async Task Http_reads_are_safe_bounded_and_never_append_operations()
    {
        await using var s = await new ResultOperationsWorkflowTests(fixture).Create(true); _ = await BacktestWorkflowTests.Definition(s);
        var scope = await s.Query(); var suffix = $"?Competition={scope.CompetitionId}&Season={scope.SeasonId}";
        var before = await s.Db.DatasetArtifacts.AsNoTracking().Select(a => a.Content).ToArrayAsync();
        var operations = await s.Db.DatasetBuildEvents.CountAsync(); var imports = await s.Db.IngestionRuns.CountAsync(); var backtests = await s.Db.BacktestOperations.CountAsync();
        await using var host = new Host(fixture.GetConnectionString(), s.Root); using var client = host.CreateClient(new() { BaseAddress = new("http://localhost") });
        foreach (var route in new[] { "fixtures", "competitions", "seasons", "teams", "models", "predictions", "backtests", "quality", "provenance" })
        {
            using var response = await client.GetAsync("/api/v1/dashboard/" + route + suffix); Assert.True(response.StatusCode == HttpStatusCode.OK, route + ": " + response.StatusCode + " " + await response.Content.ReadAsStringAsync());
            var json = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("storageKey", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("canonicalJson", json, StringComparison.OrdinalIgnoreCase);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/dashboard/fixtures?Limit=101")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/api/v1/dashboard/fixtures", new StringContent("{}"))).StatusCode);
        using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/v1/dashboard/fixtures"); foreign.Headers.Host = "evil.example"; Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);
        var after = await s.Db.DatasetArtifacts.AsNoTracking().Select(a => a.Content).ToArrayAsync(); Assert.Equal(before.Length, after.Length); Assert.Equal(before.Select(Convert.ToHexString), after.Select(Convert.ToHexString));
        Assert.Equal(operations, await s.Db.DatasetBuildEvents.CountAsync()); Assert.Equal(imports, await s.Db.IngestionRuns.CountAsync()); Assert.Equal(backtests, await s.Db.BacktestOperations.CountAsync());
    }
    [Fact]
    public async Task Demo_is_opt_in_and_production_never_exposes_it()
    {
        await using var host = new Host(fixture.GetConnectionString(), Path.GetTempPath(), true); using var client = host.CreateClient();
        var text = await client.GetStringAsync("/api/v1/dashboard/fixtures"); Assert.Contains("\"demo\":true", text); Assert.Contains("DEMO", text);
        await using var prod = new Host(fixture.GetConnectionString(), Path.GetTempPath(), true, "Production"); using var prodClient = prod.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Forbidden, (await prodClient.GetAsync("/api/v1/dashboard/fixtures")).StatusCode);
    }
    [Theory]
    [InlineData("loading")]
    [InlineData("empty")]
    [InlineData("denied")]
    [InlineData("error")]
    [InlineData("ready")]
    public async Task Components_render_read_states_and_demo_in_both_languages(string state)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        foreach (var language in new[] { "pl-PL", "en-GB" })
        {
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<ReadState>(ParameterView.FromDictionary(new Dictionary<string, object?> { { "State", state }, { "Demo", true }, { "Language", language } })); return output.ToHtmlString();
            });
            Assert.Contains("DEMO", html);
            if (state != "ready") Assert.Contains("aria-live=", html);
            if (state is "denied" or "error") Assert.Contains("alert", html);
        }
    }
    [Fact]
    public async Task Governed_reads_page_stably_and_preserve_all_artifact_bytes_and_ledgers()
    {
        await using var s = await new ResultOperationsWorkflowTests(fixture).Create(true);
        var definition = await BacktestWorkflowTests.Definition(s, true);
        var finalized = await s.Get<IHistoricalBacktests>().RunAsync(new(Guid.NewGuid(), definition, "operator", "Dashboard test fixture", true));
        var before = await s.Db.Backtests.AsNoTracking().SingleAsync(a => a.Id == finalized.SnapshotId);
        var ledger = await s.Db.BacktestOperations.CountAsync(); var imports = await s.Db.IngestionRuns.CountAsync(); var datasets = await s.Db.DatasetBuildEvents.CountAsync();
        var query = s.Get<IDashboardQueries>(); var scope = (await s.Query()); var filter = new DashboardFilter(scope.CompetitionId, scope.SeasonId, Limit: 1);
        var first = await query.FixturesAsync(filter); var again = await query.FixturesAsync(filter);
        Assert.Single(first.Items); Assert.Equal(first.Items, again.Items); Assert.Empty((await query.FixturesAsync(filter with { Offset = 1 })).Items);
        Assert.Single((await query.ChoicesAsync("competitions", filter)).Items);
        Assert.Single((await query.ChoicesAsync("seasons", filter)).Items);
        Assert.NotEmpty((await query.ChoicesAsync("teams", filter)).Items);
        var reports = await query.BacktestsAsync(filter); Assert.Single(reports.Items); Assert.NotEmpty(reports.Items[0].Predictions);
        Assert.All(reports.Items[0].Metrics, m => { if (!m.MinimumSamplesMet) Assert.Null(m.Value); });
        Assert.Equal("snapshot_only_not_certified", (await query.QualityAsync(filter)).Readiness);
        var after = await s.Db.Backtests.AsNoTracking().SingleAsync(a => a.Id == before.Id); Assert.Equal(before.Content, after.Content); Assert.Equal(before.Hash, after.Hash); Assert.Equal(before.RecordedAtUtc, after.RecordedAtUtc);
        Assert.Equal(ledger, await s.Db.BacktestOperations.CountAsync()); Assert.Equal(imports, await s.Db.IngestionRuns.CountAsync()); Assert.Equal(datasets, await s.Db.DatasetBuildEvents.CountAsync());
        var policy = await s.Db.SourcePolicies.Include(p => p.Audit).SingleAsync(p => p.DataSourceId == s.Source);
        policy.Revoke(Guid.NewGuid(), "operator", "Display rights revoked", await s.Now()); await s.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => query.BacktestsAsync(filter));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => query.FixturesAsync(filter));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => query.ChoicesAsync("teams", filter));
    }
    [Fact]
    public async Task Analytics_rights_do_not_grant_display_rights()
    {
        await using var s = await new ResultOperationsWorkflowTests(fixture).Create(); var d = await BacktestWorkflowTests.Definition(s);
        var scope = await s.Query(); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Get<IDashboardQueries>().FixturesAsync(new(scope.CompetitionId, scope.SeasonId)));
        Assert.NotEqual(Guid.Empty, d.DatasetId);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_or_corrupt_raw_prevents_display(bool corrupt)
    {
        await using var s = await new ResultOperationsWorkflowTests(fixture).Create(true); _ = await BacktestWorkflowTests.Definition(s);
        var raw = await s.Db.RawPayloads.FirstAsync(r => r.DataSourceId == s.Source); var path = Path.Combine(s.Root, raw.StorageKey + ".raw");
        if (corrupt) await File.WriteAllTextAsync(path, "corrupt"); else File.Delete(path);
        var scope = await s.Query(); await Assert.ThrowsAnyAsync<Exception>(() => s.Get<IDashboardQueries>().FixturesAsync(new(scope.CompetitionId, scope.SeasonId)));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Changed_current_attribution_or_retention_restrictions_block_stored_artifacts(bool attribution)
    {
        await using var s = await new ResultOperationsWorkflowTests(fixture).Create(true); _ = await BacktestWorkflowTests.Definition(s);
        var policy = await s.Db.SourcePolicies.Include(p => p.Audit).SingleAsync(p => p.DataSourceId == s.Source); var now = await s.Now();
        policy.Revoke(Guid.NewGuid(), "operator", "Tighter display terms", now); await s.Db.SaveChangesAsync(); now = await s.Now();
        var replacement = new SourcePolicy(Guid.NewGuid(), s.Source, policy.Version + 1, now, null, "fictional terms", "fictional evidence", now,
            new[] { DataPurpose.DataRetrieval, DataPurpose.RawPayloadStorage, DataPurpose.HistoricalRetention, DataPurpose.InternalAnalytics, DataPurpose.PublicDisplay }
            .Select(p => new PurposePermission(p, PermissionDecision.Allowed, attribution ? "Required fictional credit" : null, attribution ? null : 1)));
        s.Db.Add(replacement); await s.Db.SaveChangesAsync(); replacement.Approve(Guid.NewGuid(), "operator", "Reviewed fictional restrictions", now, now); await s.Db.SaveChangesAsync();
        var scope = await s.Query(); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Get<IDashboardQueries>().FixturesAsync(new(scope.CompetitionId, scope.SeasonId)));
    }
}
