using System.Text.Json;
using BetStats.Application.Datasets;
using BetStats.Application.Ingestion;
using BetStats.Application.Pipeline;
using BetStats.Infrastructure.Pipeline;
using BetStats.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BetStats.Application.Coverage;
using BetStats.Application.Evaluation;
using BetStats.Application.Football;
using BetStats.Application.Models;
using BetStats.Domain.Coverage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;

namespace BetStats.IntegrationTests;

public sealed class FootballValidationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class Loopback : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        { app.Use((context, following) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; return following(); }); next(app); };
    }
    private sealed class ApiHost(string connection, string root, string environment = "Development") : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment(environment)
            .UseSetting("ConnectionStrings:BetStats", connection).UseSetting("Ingestion:RawStoragePath", root)
            .ConfigureServices(s => s.AddSingleton<IStartupFilter, Loopback>());
    }
    private static readonly PipelineApproval Approval = new("test:validation", "Explicit fictional validation", true);
    private async Task<ResultOperationsWorkflowTests.Scenario> Create() => await new ResultOperationsWorkflowTests(fixture).Create();
    private static async Task<FootballValidationInput> Input(ResultOperationsWorkflowTests.Scenario s)
    {
        var raw = await s.Db.RawPayloads.AsNoTracking().Where(r => r.DataSourceId == s.Source && r.ExternalReference == "fixture:results-v1").FirstAsync();
        return new(1, s.Source, raw.Id, raw.ContentHashSha256, "results-v1", s.Fixture.Scope, ValidationEvidenceKind.Synthetic, []);
    }
    private static async Task<Guid> Plan(ResultOperationsWorkflowTests.Scenario s, FootballValidationInput input, int lease = 300)
    {
        var job = Guid.NewGuid(); var jobs = s.Get<PostgreSqlPipeline>();
        await jobs.PlanAsync(job, new(1, PipelineKind.FootballValidation, new(await s.Now()), JsonSerializer.Serialize(input), LeaseSeconds: lease), Approval);
        await jobs.SetEnabledAsync(job, true, Approval); return job;
    }
    private static Task<int> Command(ResultOperationsWorkflowTests.Scenario s, string action, Guid? execution = null, bool development = true) =>
        PipelineOperatorCommand.RunAsync(s.Scope.ServiceProvider, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Pipeline:Action"] = action, ["Pipeline:Actor"] = Approval.Actor, ["Pipeline:Reason"] = Approval.Reason,
            ["Pipeline:Approve"] = "true", ["Pipeline:ExecutionId"] = execution?.ToString() }).Build(), development);
    [Fact] public async Task Authorized_synthetic_dataset_is_inspected_without_historical_forecast_or_accuracy_claims()
    {
        await using var s = await Create(); var report = await s.Get<FootballValidation>().BuildAsync(await Input(s));
        Assert.Equal(ValidationEvidenceKind.Synthetic, report.EvidenceKind); Assert.NotEqual(ValidationReadiness.Verified, report.Readiness);
        Assert.True(report.ImportedEvents > 0); Assert.True(report.MissingResults > 0); Assert.True(report.UnknownKickoffs > 0);
        Assert.Empty(report.Models); Assert.Contains("real_data_validation_unavailable", report.Limitations);
        Assert.Contains("no_eligible_evaluation_samples", report.Limitations);
        Assert.Equal(report.InputHash, CanonicalDatasetJson.Fingerprint(await Input(s)));
    }
    [Theory] [InlineData("scope")] [InlineData("hash")] [InlineData("profile")]
    public async Task Raw_scope_hash_and_profile_are_bound(string mismatch)
    {
        await using var s = await Create(); var input = await Input(s);
        input = mismatch switch { "scope" => input with { Scope = new("other", "other") }, "hash" => input with { ExpectedRawHash = new string('a', 64) }, _ => input with { Profile = "football-history-v1" } };
        await Assert.ThrowsAsync<InvalidDataException>(() => s.Get<FootballValidation>().BuildAsync(input));
    }
    [Fact] public async Task Missing_authorization_and_revocation_deny_validation_and_stored_report_reads()
    {
        await using var s = await Create(); var input = await Input(s);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Get<FootballValidation>().BuildAsync(input with { SourceId = Guid.NewGuid() }));
        var job = await Plan(s, input); Assert.Equal(0, await Command(s, "run-once"));
        var e = await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(x => x.JobId == job);
        s.Db.ChangeTracker.Clear(); var policy = await s.Db.SourcePolicies.Include(p => p.Audit).SingleAsync(p => p.DataSourceId == s.Source);
        policy.Revoke(Guid.NewGuid(), "test:review", "Withdraw fictional permission", await s.Now()); await s.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Get<FootballValidation>().BuildAsync(input));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Get<FootballValidation>().ReadAsync(e.Id, false));
    }
    [Fact] public async Task Repeated_dispatch_and_reads_preserve_frozen_report_and_all_legacy_artifacts()
    {
        await using var s = await Create(); var legacy = await BacktestWorkflowTests.Definition(s, true);
        var before = await s.Db.Backtests.AsNoTracking().Select(a => a.Content).ToArrayAsync();
        var job = await Plan(s, await Input(s)); Assert.Equal(0, await Command(s, "run-once"));
        var e = await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(x => x.JobId == job);
        var one = await s.Get<FootballValidation>().ReadAsync(e.Id, false);
        var operations = await s.Db.BacktestOperations.CountAsync(); var imports = await s.Db.IngestionRuns.CountAsync();
        Assert.Equal(0, await Command(s, "run-once")); Assert.Equal(0, await Command(s, "verify-validation", e.Id));
        var two = await s.Get<FootballValidation>().ReadAsync(e.Id, false);
        Assert.Equal(CanonicalDatasetJson.Fingerprint(one), CanonicalDatasetJson.Fingerprint(two));
        Assert.Equal(operations, await s.Db.BacktestOperations.CountAsync()); Assert.Equal(imports, await s.Db.IngestionRuns.CountAsync());
        Assert.Equal(before.Select(Convert.ToHexString), (await s.Db.Backtests.AsNoTracking().Select(a => a.Content).ToArrayAsync()).Select(Convert.ToHexString));
        Assert.Single(await s.Db.Set<PipelineArtifact>().Where(a => a.ExecutionId == e.Id).ToArrayAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<PostgreSqlPipeline>().RetryAsync(e.Id, true, Approval));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Get<FootballValidation>().ReadAsync(e.Id, true)); // display is separately denied
    }
    [Fact] public async Task Corrupt_raw_prevents_both_execution_and_report_retrieval()
    {
        await using var s = await Create(); var input = await Input(s); var job = await Plan(s, input); Assert.Equal(0, await Command(s, "run-once"));
        var e = await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(x => x.JobId == job);
        var raw = await s.Db.RawPayloads.AsNoTracking().SingleAsync(x => x.Id == input.RawId);
        await File.WriteAllTextAsync(Path.Combine(s.Root, raw.StorageKey + ".raw"), "corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => s.Get<FootballValidation>().ReadAsync(e.Id, false));
        await Plan(s, input); Assert.Equal(1, await Command(s, "run-once"));
    }
    [Fact] public async Task Validation_uses_existing_live_ownership_and_expired_recovery_without_duplicate_publication()
    {
        await using var s = await Create(); var job = await Plan(s, await Input(s), 10); var jobs = s.Get<PostgreSqlPipeline>();
        var claim = (await jobs.AcquireAsync(Approval))!;
        await using (var owner = await jobs.OwnAsync(claim.ExecutionId))
        {
            await Task.Delay(10200); await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.RetryAsync(claim.ExecutionId, true, Approval));
        }
        Assert.Equal(0, await Command(s, "recover", claim.ExecutionId));
        Assert.Single(await s.Db.Set<PipelineArtifact>().Where(a => a.ExecutionId == claim.ExecutionId).ToArrayAsync());
        Assert.Equal(2, (await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(x => x.JobId == job)).Attempt);
    }
    [Fact] public async Task Development_gate_and_bilingual_report_labeling_are_explicit()
    {
        await using var s = await Create(); await Assert.ThrowsAsync<InvalidOperationException>(() => Command(s, "status", development: false));
        var report = await s.Get<FootballValidation>().BuildAsync(await Input(s));
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        foreach (var language in new[] { "pl-PL", "en-GB" })
        {
            var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<ValidationReport>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                ["Artifact"] = new FootballValidationArtifact(Guid.NewGuid(), new string('a', 64), report), ["Language"] = language }))).ToHtmlString());
            Assert.Contains("DEMO", html); Assert.Contains("SHA256", html);
        }
    }
    [Fact] public async Task Http_report_is_read_only_and_production_is_denied()
    {
        await using var s = await new ResultOperationsWorkflowTests(fixture).Create(true);
        var job = await Plan(s, await Input(s)); Assert.Equal(0, await Command(s, "run-once"));
        var execution = await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(e => e.JobId == job);
        var before = CanonicalDatasetJson.Fingerprint(await s.Db.Set<PipelineArtifact>().AsNoTracking().OrderBy(a => a.ExecutionId).ToArrayAsync());
        var receipts = await s.Db.Set<PipelineReceipt>().CountAsync(); var imports = await s.Db.IngestionRuns.CountAsync(); var operations = await s.Db.BacktestOperations.CountAsync();
        await using var host = new ApiHost(fixture.GetConnectionString(), s.Root); using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        var url = "/api/v1/dashboard/validation/" + execution.Id;
        var text = await client.GetStringAsync(url); Assert.Contains("Synthetic", text); Assert.DoesNotContain(s.Root, text); Assert.DoesNotContain("StorageKey", text);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync(url, null)).StatusCode);
        Assert.Equal(before, CanonicalDatasetJson.Fingerprint(await s.Db.Set<PipelineArtifact>().AsNoTracking().OrderBy(a => a.ExecutionId).ToArrayAsync()));
        Assert.Equal(receipts, await s.Db.Set<PipelineReceipt>().CountAsync()); Assert.Equal(imports, await s.Db.IngestionRuns.CountAsync()); Assert.Equal(operations, await s.Db.BacktestOperations.CountAsync());
        await using var production = new ApiHost(fixture.GetConnectionString(), s.Root, "Production"); using var prod = production.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Forbidden, (await prod.GetAsync(url)).StatusCode);
    }
    [Fact] public async Task All_existing_models_bind_to_frozen_pipeline_publications_without_inventing_evaluation()
    {
        await using var s = await Create(); var request = await s.Request();
        var target = await s.Db.Observations.SingleAsync(o => o.Id == request.Metadata.Definition.Targets[0].DateObservationId);
        var identity = await s.Db.ProviderIdentities.SingleAsync(i => i.Id == target.ProviderIdentityId);
        var value = new EventTimeValue(s.Fixture.TargetDate, new(12, 0), null, 0, null, EventTimePrecision.Minute);
        var raw = await s.Raw(new EventTimeSourceClaim(1, target.RawPayloadId!.Value, identity.ExternalId, new(s.Fixture.Scope.CompetitionReference, s.Fixture.Scope.SeasonReference), value));
        await s.Get<IHistoricalCoverage>().RecordTimeAsync(new(target.Id, raw.Id, value, "synthetic-source-bound-kickoff", null, null, raw.RetrievedAtUtc, Approval.Actor, Approval.Reason));
        var baseDefinition = await BacktestWorkflowTests.Definition(s, true); var references = new List<ValidationPublication>();
        foreach (var kind in Enum.GetValues<FootballModelKind>())
        {
            var model = new FootballModelDefinition(1, kind, new(), new());
            var now = await s.Now(); var planned = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddSeconds(-2);
            var horizon = checked((int)(s.Fixture.TargetDate.ToDateTime(new(12, 0), DateTimeKind.Utc) - planned).TotalSeconds);
            var job = Guid.NewGuid(); var jobs = s.Get<PostgreSqlPipeline>();
            await jobs.PlanAsync(job, new(1, PipelineKind.PrematchPrediction, new(planned), JsonSerializer.Serialize(new PrematchInput(request,
                baseDefinition with { Model = model, Predictor = model.Predictor })), PredictionHorizonSeconds: horizon), Approval);
            await jobs.SetEnabledAsync(job, true, Approval); Assert.Equal(0, await Command(s, "run-once"));
            references.Add(new((await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(e => e.JobId == job)).Id));
        }
        var input = (await Input(s)) with { Publications = references };
        var report = await s.Get<FootballValidation>().BuildAsync(input);
        Assert.Equal(3, report.Models.Count); Assert.All(report.Models, m => { Assert.True(m.Predictions > 0); Assert.Empty(m.Targets); Assert.True(m.PublishedUtc > m.ActualCutoffUtc); });
        Assert.Contains("no_eligible_evaluation_samples", report.Limitations);
        Assert.NotEqual(ValidationReadiness.Verified, report.Readiness);
        var validationJob = await Plan(s, input); Assert.Equal(0, await Command(s, "run-once"));
        var e = await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(e => e.JobId == validationJob);
        Assert.Equal(3, (await s.Get<FootballValidation>().ReadAsync(e.Id, false)).Report.Models.Count);
    }
    [Theory] [InlineData("unknown,format\n1,2\n")] [InlineData("Div,Date,HomeTeam,AwayTeam,MatchId,Status,ResultBasis,FTHG,FTAG,HTHG,HTAG,PublishedAtUtc\nFICT,01/01/2020,A,B,m,Finished,RegulationTime,1,0,,,\nFICT,01/01/2020,A,B,m,Finished,RegulationTime,2,0,,,\n")]
    public async Task Invalid_or_conflicting_captured_csv_is_blocked_without_guessing(string csv)
    {
        await using var s = await Create(); var persistence = s.Get<IFootballIngestionPersistence>();
        var attempt = await persistence.BeginAsync(Guid.NewGuid(), s.Source, default); var bytes = System.Text.Encoding.UTF8.GetBytes(csv);
        var store = s.Get<IRawPayloadStore>(); var stored = await store.StageAsync(bytes);
        var raw = await persistence.CaptureAsync(attempt, new(bytes, "text/csv", await s.Now(), "results-v1"), stored, s.Fixture.Scope, default);
        await store.FinalizeAsync(stored);
        var input = new FootballValidationInput(1, s.Source, raw.Id, stored.Hash, "results-v1", s.Fixture.Scope, ValidationEvidenceKind.Synthetic, []);
        var report = await s.Get<FootballValidation>().BuildAsync(input);
        Assert.Equal(ValidationReadiness.Blocked, report.Readiness); Assert.NotEmpty(report.Issues); Assert.Equal(0, report.ImportedEvents);
    }
}
