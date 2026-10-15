using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BetStats.Application.Datasets;
using BetStats.Application.Governance;
using BetStats.Application.Identity;
using BetStats.Application.Ingestion;
using BetStats.Application.Pipeline;
using BetStats.Application.Providers;
using BetStats.Application.Quality;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using BetStats.Infrastructure.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using BetStats.Web.Components;

namespace BetStats.IntegrationTests;

// No test can reach the Internet: every provider transport is a controlled message handler.
[Trait("Category", "Security")]
public sealed class ProviderActivationWorkflowTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly PipelineApproval Approval = new("test:activation", "Controlled synthetic provider authorization", true);
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond = (_, _) => Task.FromResult(Response());
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            Assert.Equal("v3.football.api-sports.io", request.RequestUri!.Host);
            Assert.Equal("/fixtures", request.RequestUri.AbsolutePath);
            Assert.DoesNotContain("page=", request.RequestUri.Query);
            return Respond(request, token);
        }
    }
    private sealed class FaultStore(IRawPayloadStore inner) : IRawPayloadStore
    {
        public bool FailStage, FailFinalize;
        public Task<StoredPayload> StageAsync(ReadOnlyMemory<byte> b, CancellationToken t = default) => FailStage ? throw new IOException("controlled lost response") : inner.StageAsync(b, t);
        public Task FinalizeAsync(StoredPayload p, CancellationToken t = default) => FailFinalize ? throw new IOException("controlled post-capture interruption") : inner.FinalizeAsync(p, t);
        public Task<ReadOnlyMemory<byte>> ReadAsync(StoredPayload p, CancellationToken t = default) => inner.ReadAsync(p, t);
        public IReadOnlyList<string> InventoryStaged() => inner.InventoryStaged();
    }
    private sealed record Setup(ProviderPublicationWorkflowTests.Setup Publication, Handler Handler, ProviderActivationGrant Grant, FaultStore Store)
    {
        public ResultOperationsWorkflowTests.Scenario S => Publication.Scenario;
        public ProviderActivation Activation => S.Get<ProviderActivation>();
    }
    private static HttpResponseMessage Response(int status = 200, string? json = null) => new((HttpStatusCode)status) {
        Content = new StringContent(json ?? ProviderPublicationWorkflowTests.Payload().ToJsonString(), Encoding.UTF8, "application/json") };
    private async Task<Setup> Create(bool enabled = true, bool credentials = true, bool development = true, bool reviewed = true, int budget = 3, int interval = 1, string? restriction = null, bool controlled = true)
    {
        var handler = new Handler(); FaultStore? store = null;
        var publication = await new ProviderPublicationWorkflowTests(fixture).Create(restriction, reviewed, services => {
            var original = services.Last(x => x.ServiceType == typeof(IRawPayloadStore));
            services.AddSingleton<IRawPayloadStore>(p => store = new FaultStore((IRawPayloadStore)original.ImplementationFactory!(p)));
            services.AddSingleton(new ProviderTransportOptions(development, enabled, () => credentials ? "synthetic-secret" : null));
            services.AddSingleton(new ApiFootballHttpTransport(new HttpMessageInvoker(handler), controlled));
        });
        var s = publication.Scenario;
        var code = await s.Db.DataSources.Where(x => x.Id == s.Source).Select(x => x.Code).SingleAsync();
        var grant = new ProviderActivationGrant(1, Guid.NewGuid(), s.Source, code, publication.PolicyId, 1,
            new(1, 999999, 2026, new(2026, 10, 13), new(2026, 10, 19)), AcquisitionCategory.PrematchMetadata,
            "synthetic:owned-permission", "synthetic:verified-test-entitlement", "test:reviewer", (await s.Now()).AddDays(1), budget, interval, 1, true, true, ValidationEvidenceKind.Synthetic, true);
        return new(publication, handler, grant, store!);
    }
    private static async Task<ProviderAcquisitionInput> Activate(Setup f)
    {
        await f.Activation.RegisterAsync(f.Grant, Approval); await f.Activation.SetEnabledAsync(f.Grant.Id, true, Approval);
        return await f.Activation.PlanAsync(f.Grant.Id);
    }
    private static async Task<PipelineClaim> Claim(Setup f, ProviderAcquisitionInput input)
    {
        var jobs = f.S.Get<PostgreSqlPipeline>(); var id = Guid.NewGuid();
        await jobs.PlanAsync(id, new(1, PipelineKind.ProviderAcquisition, new(await f.S.Now()), JsonSerializer.Serialize(input)), Approval);
        await jobs.SetEnabledAsync(id, true, Approval); return (await jobs.AcquireAsync(Approval))!;
    }
    private static Task<(PipelineOutcome Outcome, object? Result)> Execute(Setup f, PipelineClaim c, ProviderAcquisitionInput i, CancellationToken t = default) => f.Activation.ExecuteAsync(c, i, Approval, t);
    private static async Task<ProviderPublicationArtifact> Publish(Setup f)
    {
        var status = await f.Activation.StatusAsync(f.S.Source, false); var jobs = f.S.Get<PostgreSqlPipeline>();
        await jobs.SetEnabledAsync(status.PublicationJobId!.Value, true, Approval); var claim = (await jobs.AcquireAsync(Approval))!;
        var (outcome, bytes) = await f.S.Get<PipelineWork>().ExecuteAsync(claim, Approval, default);
        await jobs.CompleteAsync(claim, outcome, Approval, artifact: bytes);
        return Assert.Single((await f.S.Get<ProviderPublication>().StatusAsync(claim.JobId, false)).Receipts);
    }
    [Theory] [InlineData("authorization")] [InlineData("credential")] [InlineData("disabled")]
    [InlineData("production")] [InlineData("flag")] [InlineData("expired")] [InlineData("hash")] [InlineData("revoked")]
    [InlineData("league")] [InlineData("season")] [InlineData("capability")] [InlineData("retention")] [InlineData("attribution")] [InlineData("synthetic-live")] [InlineData("synthetic-real")]
    public async Task Activation_and_acquisition_fail_closed_without_network(string fault)
    {
        var f = await Create(credentials: fault != "credential", development: fault != "production", enabled: fault != "flag",
            restriction: fault is "retention" or "attribution" ? fault : null, controlled: fault != "synthetic-live"); await using var s = f.S;
        if (fault == "synthetic-real") f = f with { Grant = f.Grant with { EvidenceKind = ValidationEvidenceKind.OperatorDeclaredReal, PermissionEvidence = "operator:review", EntitlementEvidence = "operator:account" } };
        if (fault is "credential" or "production" or "flag" or "retention" or "attribution" or "synthetic-live" or "synthetic-real")
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Activate(f)); Assert.Equal(0, f.Handler.Calls); return;
        }
        if (fault is "league" or "season" or "capability")
        {
            var bad = fault == "league" ? f.Grant with { Canary = f.Grant.Canary with { League = 0 } } : fault == "season" ? f.Grant with { Canary = f.Grant.Canary with { Season = 0 } } : f.Grant with { Category = (AcquisitionCategory)999 };
            await Assert.ThrowsAsync<ArgumentException>(() => f.Activation.RegisterAsync(bad, Approval)); Assert.Equal(0, f.Handler.Calls); return;
        }
        if (fault == "expired")
        {
            var expired = (await s.Now()).AddDays(-1);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Activation.RegisterAsync(f.Grant with { ExpiresUtc = expired }, Approval));
            Assert.Equal(0, f.Handler.Calls); return;
        }
        var input = fault == "authorization" ? new ProviderAcquisitionInput(1, f.Grant.Id, new string('0', 64)) : await Activate(f);
        var claim = await Claim(f, input);
        if (fault == "disabled") await f.Activation.SetEnabledAsync(f.Grant.Id, false, Approval);
        if (fault == "revoked") { var policy = await s.Db.SourcePolicies.Include(x => x.Audit).SingleAsync(x => x.Id == f.Grant.PolicyId); var now = await s.Now(); policy.Revoke(Guid.NewGuid(), "test:revoke", "Revoke controlled grant", now); await s.Db.SaveChangesAsync(); }
        if (fault == "hash") input = input with { GrantHash = new string('0',64) };
        await Assert.ThrowsAnyAsync<Exception>(() => Execute(f, claim, input)); Assert.Equal(0, f.Handler.Calls);
        Assert.Empty(await s.Db.Set<ProviderRequestEvent>().Where(x => x.SourceId == s.Source).ToArrayAsync());
    }
    [Fact] public async Task Controlled_http_raw_publication_and_recovery_preserve_exact_original_bytes()
    {
        var f = await Create(); await using var s = f.S; var input = await Activate(f); var claim = await Claim(f, input);
        var bytes = ProviderPublicationWorkflowTests.Payload().ToJsonString(); f.Handler.Respond = (_, _) => Task.FromResult(Response(json: bytes));
        var result = await Execute(f, claim, input); Assert.Equal(PipelineState.Completed, result.Outcome.State); Assert.Equal(1, f.Handler.Calls);
        var before = await f.Activation.StatusAsync(s.Source, false); Assert.Equal(1, before.UsedRequests); Assert.Equal(2, before.RemainingRequests); Assert.Null(before.ProviderRemainingQuota);
        var raw = await s.Db.RawPayloads.SingleAsync(x => x.Id == before.LatestRawId);
        Assert.Equal(bytes, Encoding.UTF8.GetString((await f.Store.ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, raw.ByteLength!.Value))).Span));
        Assert.Equal(PipelineState.Disabled, before.Publication!.State); Assert.True(raw.RecordedAtUtc >= raw.RetrievedAtUtc);
        // Child capture/preparation committed, parent has not acknowledged: repeat uses RAW only.
        Assert.Equal(PipelineState.Completed, (await Execute(f, claim, input)).Outcome.State); Assert.Equal(1, f.Handler.Calls);
        var receipt = await Publish(f); Assert.True(receipt.Verified); Assert.Equal(ProviderPublicationState.Published, receipt.Report.State);
        Assert.Equal(raw.Id, receipt.Report.Input.RawId); Assert.Null(Assert.Single(receipt.Report.Fixtures).SourcePublicationUtc);
        var bridge = await s.Get<IFootballAcquisitionReports>().AcquisitionAsync(receipt.Report.ExecutionId, false);
        Assert.Equal(ValidationEvidenceKind.Synthetic, bridge.Report.EvidenceKind);
    }
    [Theory] [InlineData(401)] [InlineData(403)] [InlineData(429)] [InlineData(500)] [InlineData(302)]
    public async Task Http_failures_consume_budget_and_do_not_retry_or_leak(int code)
    {
        var f = await Create(); await using var s = f.S; var input = await Activate(f); var claim = await Claim(f, input);
        f.Handler.Respond = (_, _) => { var r = Response(code, "synthetic-secret"); if (code == 429) r.Headers.RetryAfter = new(TimeSpan.FromHours(2)); return Task.FromResult(r); };
        Assert.Equal(PipelineState.Blocked, (await Execute(f, claim, input)).Outcome.State);
        Assert.Equal(PipelineState.Blocked, (await Execute(f, claim, input)).Outcome.State); Assert.Equal(1, f.Handler.Calls);
        var status = await f.Activation.StatusAsync(s.Source, false); Assert.Equal(1, status.UsedRequests); Assert.Null(status.LatestRawId);
        Assert.DoesNotContain("synthetic-secret", JsonSerializer.Serialize(status));
        if (code == 429) { Assert.True(status.NextRequestUtc > (await s.Now()).AddHours(1)); var next = await Claim(f, input); Assert.Equal("provider_rate_limit_wait", (await Execute(f, next, input)).Outcome.Category); Assert.Equal(1, f.Handler.Calls); }
    }
    [Theory] [InlineData("malformed")] [InlineData("duplicate")] [InlineData("pagination")] [InlineData("league")] [InlineData("season")]
    public async Task Invalid_json_scope_and_pagination_preserve_raw_but_never_publish(string fault)
    {
        var f = await Create(); await using var s = f.S; var input = await Activate(f); var claim = await Claim(f, input);
        var json = ProviderPublicationWorkflowTests.Payload();
        if (fault == "duplicate") { json["response"]!.AsArray().Add(json["response"]![0]!.DeepClone()); json["results"] = 2; }
        if (fault == "pagination") json["paging"]!["total"] = 2;
        if (fault == "league") json["response"]![0]!["league"]!["id"] = 1;
        if (fault == "season") json["response"]![0]!["league"]!["season"] = 2025;
        f.Handler.Respond = (_, _) => Task.FromResult(Response(json: fault == "malformed" ? "{" : json.ToJsonString()));
        Assert.Equal("provider_original_raw_invalid", (await Execute(f, claim, input)).Outcome.Category);
        Assert.NotNull((await f.Activation.StatusAsync(s.Source, false)).LatestRawId); Assert.Equal(1, f.Handler.Calls);
        Assert.Equal("provider_original_raw_invalid", (await Execute(f, claim, input)).Outcome.Category); Assert.Equal(1, f.Handler.Calls);
        Assert.Empty(await s.Db.Observations.Where(x => x.DataSourceId == s.Source).ToArrayAsync());
    }
    [Theory] [InlineData("timeout")] [InlineData("size")] [InlineData("cancel")]
    public async Task Timeout_size_and_cancellation_are_bounded(string fault)
    {
        var f = await Create(); await using var s = f.S; var input = await Activate(f); var claim = await Claim(f, input);
        using var cancel = new CancellationTokenSource();
        f.Handler.Respond = async (_, t) => { if (fault == "size") return Response(json: new string('x', 1048577)); if (fault == "cancel") cancel.Cancel(); await Task.Delay(TimeSpan.FromSeconds(10), t); return Response(); };
        if (fault == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(f, claim, input, cancel.Token));
        else Assert.Equal(PipelineState.Blocked, (await Execute(f, claim, input)).Outcome.State);
        Assert.Equal(1, f.Handler.Calls); Assert.Equal(1, (await f.Activation.StatusAsync(s.Source, false)).UsedRequests);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Crash_after_response_or_raw_never_causes_automatic_download(bool afterRaw)
    {
        var f = await Create(); await using var s = f.S; var input = await Activate(f); var claim = await Claim(f, input);
        f.Store.FailStage = !afterRaw; f.Store.FailFinalize = afterRaw;
        await Assert.ThrowsAsync<IOException>(() => Execute(f, claim, input)); Assert.Equal(1, f.Handler.Calls);
        f.Store.FailStage = false; f.Store.FailFinalize = false;
        var outcome = (await Execute(f, claim, input)).Outcome;
        Assert.Equal(afterRaw ? PipelineState.Completed : PipelineState.Blocked, outcome.State); Assert.Equal(1, f.Handler.Calls);
        Assert.Equal(1, (await f.Activation.StatusAsync(s.Source, false)).UsedRequests);
        if (!afterRaw)
        {
            await s.Get<PostgreSqlPipeline>().CompleteAsync(claim, outcome, Approval);
            await f.Activation.ReconcileAsync(claim.ExecutionId, Approval);
            Assert.Equal(1, (await f.Activation.StatusAsync(s.Source, false)).UsedRequests);
        }
    }
    [Fact] public async Task Stale_worker_disable_and_revocation_during_http_cannot_capture()
    {
        var f = await Create(); await using var s = f.S; var input = await Activate(f); var claim = await Claim(f, input);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(f, claim with { Owner = Guid.NewGuid() }, input)); Assert.Equal(0, f.Handler.Calls);
        f.Handler.Respond = async (_, _) => { await f.Activation.SetEnabledAsync(f.Grant.Id, false, Approval); return Response(); };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Execute(f, claim, input)); Assert.Equal(1, f.Handler.Calls);
        Assert.Empty(await s.Db.RawPayloads.Where(x => x.DataSourceId == s.Source).ToArrayAsync());
        var revoked = await Create(); await using var revokedScenario = revoked.S;
        var revokedInput = await Activate(revoked); var revokedClaim = await Claim(revoked, revokedInput);
        revoked.Handler.Respond = async (_, _) => {
            using var reviewerScope = revokedScenario.Provider.CreateScope();
            var reviewer = reviewerScope.ServiceProvider.GetRequiredService<BetStatsDbContext>();
            var policy = await reviewer.SourcePolicies.Include(x => x.Audit).SingleAsync(x => x.Id == revoked.Grant.PolicyId);
            var at = await reviewer.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
            policy.Revoke(Guid.NewGuid(), Approval.Actor, "Revoke while controlled HTTP is in flight", at);
            await reviewer.SaveChangesAsync(); return Response();
        };
        var denied = await Execute(revoked, revokedClaim, revokedInput);
        Assert.Equal(PipelineState.Blocked, denied.Outcome.State);
        Assert.Equal("provider_PermissionDenied", denied.Outcome.Category);
        Assert.Equal(1, revoked.Handler.Calls);
        Assert.Empty(await revokedScenario.Db.RawPayloads.Where(x => x.DataSourceId == revokedScenario.Source).ToArrayAsync());
        Assert.Equal(1, await revokedScenario.Db.Set<ProviderRequestEvent>().CountAsync(x => x.SourceId == revokedScenario.Source && x.Stage == "Reserved"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Execute(revoked, revokedClaim, revokedInput));
        Assert.Equal(1, revoked.Handler.Calls); // Neither the in-flight response nor recovery bypasses revocation.
    }
    [Fact] public async Task Concurrent_workers_share_budget_and_restart_does_not_reset_it()
    {
        var f = await Create(budget: 1); await using var s = f.S; var input = await Activate(f); var first = await Claim(f, input); var second = await Claim(f, input);
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        f.Handler.Respond = async (_, t) => { entered.SetResult(); await release.Task.WaitAsync(t); return Response(); };
        var running = Execute(f, first, input); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var otherScope = s.Provider.CreateScope(); var other = otherScope.ServiceProvider.GetRequiredService<ProviderActivation>();
        Assert.Equal("provider_budget_exhausted", (await other.ExecuteAsync(second, input, Approval, default)).Outcome.Category);
        release.SetResult(); Assert.Equal(PipelineState.Completed, (await running).Outcome.State);
        using var restarted = s.Provider.CreateScope(); var again = restarted.ServiceProvider.GetRequiredService<ProviderActivation>();
        Assert.Equal(1, (await again.StatusAsync(s.Source, false)).UsedRequests); Assert.Equal(1, f.Handler.Calls);
        // Re-register/activate cannot refund the source lifetime reservation.
        await again.RegisterAsync(f.Grant, Approval); await again.SetEnabledAsync(f.Grant.Id, true, Approval);
        Assert.Equal("provider_budget_exhausted", (await again.ExecuteAsync(second, input, Approval, default)).Outcome.Category);
    }
    [Fact] public async Task Identity_review_required_retains_original_network_raw()
    {
        var f = await Create(reviewed: false); await using var s = f.S; var input = await Activate(f); var claim = await Claim(f, input);
        Assert.Equal(PipelineState.Completed, (await Execute(f, claim, input)).Outcome.State);
        var receipt = await Publish(f); Assert.Equal(ProviderPublicationState.ReviewRequired, receipt.Report.State); Assert.Equal(1, f.Handler.Calls);
        Assert.NotEmpty(await s.Db.ProviderIdentities.Where(x => x.DataSourceId == s.Source).ToArrayAsync());
        Assert.NotNull((await f.Activation.StatusAsync(s.Source, false)).LatestRawId);
    }
    [Theory] [InlineData("pl-PL", "Autoryzowana aktywacja dostawcy")] [InlineData("en-GB", "Authorized provider activation")]
    public async Task Monitoring_is_read_only_bilingual_and_secret_free(string language, string title)
    {
        var f = await Create(); await using var s = f.S; await Activate(f);
        var status = await f.Activation.StatusAsync(s.Source, true); Assert.Equal(0, f.Handler.Calls); Assert.False(status.Operational);
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<ProviderActivationView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Status"] = status, ["Language"] = language }))).ToHtmlString());
        Assert.Contains(title, System.Net.WebUtility.HtmlDecode(html)); Assert.DoesNotContain("synthetic-secret", html); Assert.Equal(0, f.Handler.Calls);
    }
    [Fact] public async Task Activation_and_request_evidence_are_immutable_in_sql_and_application()
    {
        var f = await Create(); await using var s = f.S; var input = await Activate(f); await Execute(f, await Claim(f, input), input);
        foreach (var table in new[] { "ProviderGrants", "ProviderActivationEvents", "ProviderRequestEvents" })
        {
            var sql = "DELETE FROM ingestion.\"" + table + "\" WHERE \"SourceId\"={0}";
            await Assert.ThrowsAsync<PostgresException>(() => s.Db.Database.ExecuteSqlRawAsync(sql, s.Source));
        }
        Assert.Equal(1, (await f.Activation.StatusAsync(s.Source, false)).UsedRequests);
    }
    [Fact] public async Task Explicit_worker_commands_run_acquisition_and_parent_acknowledgment()
    {
        var f = await Create(); await using var s = f.S; var path = Path.Combine(s.Root, "grant.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(f.Grant));
        Task<int> Command(string action) => PipelineOperatorCommand.RunAsync(s.Scope.ServiceProvider, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Pipeline:Action"] = action, ["Pipeline:Actor"] = Approval.Actor, ["Pipeline:Reason"] = Approval.Reason, ["Pipeline:Approve"] = "true",
            ["Pipeline:GrantPath"] = path, ["Pipeline:GrantId"] = f.Grant.Id.ToString(), ["Pipeline:SourceId"] = s.Source.ToString() }).Build(), true);
        Assert.Equal(0, await Command("provider-register")); Assert.Equal(0, f.Handler.Calls);
        Assert.Equal(0, await Command("provider-plan")); Assert.Equal(0, await Command("provider-activate")); Assert.Equal(0, f.Handler.Calls);
        var input = await f.Activation.PlanAsync(f.Grant.Id); var jobs = s.Get<PostgreSqlPipeline>(); var job = Guid.NewGuid();
        await jobs.PlanAsync(job, new(1, PipelineKind.ProviderAcquisition, new(await s.Now()), JsonSerializer.Serialize(input)), Approval);
        await jobs.SetEnabledAsync(job, true, Approval); Assert.Equal(0, await Command("run-once"));
        var execution = await s.Db.Set<PipelineExecution>().SingleAsync(x => x.JobId == job); Assert.Equal(PipelineState.Completed, execution.State);
        var artifact = await s.Db.Set<PipelineArtifact>().SingleAsync(x => x.ExecutionId == execution.Id);
        Assert.Equal(artifact.Hash, CanonicalDatasetJson.Hash(artifact.Content)); Assert.Equal(1, f.Handler.Calls);
        Assert.DoesNotContain("synthetic-secret", Encoding.UTF8.GetString(artifact.Content));
        Assert.Equal(0, await Command("provider-status")); Assert.Equal(0, await Command("provider-disable"));
        Assert.False((await f.Activation.StatusAsync(s.Source, false)).Enabled); Assert.Equal(1, f.Handler.Calls);
    }
    [Fact] public async Task Recovery_does_not_overwrite_a_revised_publication_child()
    {
        var f=await Create(); await using var s=f.S; var input=await Activate(f); var claim=await Claim(f,input);
        await Execute(f,claim,input); var status=await f.Activation.StatusAsync(s.Source,false);
        var jobs=s.Get<PostgreSqlPipeline>(); var job=status.PublicationJobId!.Value;
        await jobs.PlanAsync(job,new(1,PipelineKind.FootballValidation,new(await s.Now()),"{}"),Approval);
        await Assert.ThrowsAsync<InvalidDataException>(()=>Execute(f,claim,input)); Assert.Equal(1,f.Handler.Calls);
        var version=await s.Db.Set<PipelineJobVersion>().Where(x=>x.JobId==job).OrderByDescending(x=>x.Version).FirstAsync();
        Assert.Equal(PipelineKind.FootballValidation,CanonicalDatasetJson.Deserialize<PipelineDefinition>(version.Content).Kind);
    }
    [Fact] public async Task Approved_identity_reprocessing_uses_network_raw_without_redownload()
    {
        var f = await Create(reviewed:false); await using var s = f.S; var input = await Activate(f); await Execute(f, await Claim(f, input), input);
        var blocked = await Publish(f); var e = await s.Db.SportingEvents.SingleAsync(x => x.Id == f.Publication.EventId);
        Guid[] targets = [e.CompetitionId, e.SeasonId!.Value, f.Publication.HomeId, f.Publication.AwayId, e.Id];
        var bindings = Assert.Single(blocked.Report.Fixtures).Bindings;
        for (var index=0; index<bindings.Count; index++)
        {
            var b = bindings[index]; var identity = await s.Db.ProviderIdentities.SingleAsync(x => x.Id == b.IdentityId);
            Assert.Equal("accepted", (await s.Get<IIdentityReview>().DecideAsync(new(identity.Id, s.Source, ReviewAction.Approve,
                new(identity.EntityKind, targets[index]), b.Version, Approval.Actor, Approval.Reason))).Result);
        }
        var jobs = s.Get<PostgreSqlPipeline>(); var retry = (await jobs.RetryAsync(blocked.Report.ExecutionId, false, Approval))!;
        var definition = JsonSerializer.Deserialize<ProviderPublicationInput>(retry.Definition.PayloadJson)!;
        var published = await s.Get<ProviderPublication>().PublishAsync(retry, definition, Approval);
        Assert.Equal(ProviderPublicationState.Published, published.Report.State); Assert.Equal(blocked.Report.Input.RawId, published.Report.Input.RawId);
        Assert.Equal(1, f.Handler.Calls); Assert.Equal(1, (await f.Activation.StatusAsync(s.Source,false)).UsedRequests);
    }
    [Fact] public async Task Approved_followup_reschedules_and_corrects_results_append_only()
    {
        var f = await Create(budget:3); await using var s = f.S; var firstInput = await Activate(f); await Execute(f, await Claim(f,firstInput), firstInput);
        var first = await Publish(f); var firstFixture = Assert.Single(first.Report.Fixtures); var cutoff = await s.Now();
        var followupGrant = f.Grant with { Id = Guid.NewGuid(), Category = AcquisitionCategory.PostmatchResults };
        await f.Activation.RegisterAsync(followupGrant, Approval); await f.Activation.SetEnabledAsync(followupGrant.Id,true,Approval);
        var followup = await f.Activation.PlanAsync(followupGrant.Id);
        async Task<ProviderPublicationArtifact> AcquireResult(int goals)
        {
            var next = (await f.Activation.StatusAsync(s.Source,false)).NextRequestUtc;
            if (next > await s.Now()) await Task.Delay(next.Value - await s.Now() + TimeSpan.FromMilliseconds(20));
            f.Handler.Respond = (_,_) => Task.FromResult(Response(json:ProviderPublicationWorkflowTests.Payload("FT",goals,20).ToJsonString()));
            Assert.Equal(PipelineState.Completed,(await Execute(f,await Claim(f,followup),followup)).Outcome.State);
            return await Publish(f);
        }
        var final = await AcquireResult(2); var correction = await AcquireResult(3);
        var finalFixture = Assert.Single(final.Report.Fixtures); var correctedFixture = Assert.Single(correction.Report.Fixtures);
        Assert.Equal(firstFixture.ScheduleObservationId, finalFixture.ScheduleCorrectsId);
        Assert.Equal(finalFixture.ResultObservationId, correctedFixture.ResultCorrectsId);
        Assert.Equal(3,f.Handler.Calls); Assert.Equal(3,(await f.Activation.StatusAsync(s.Source,false)).UsedRequests);
        Assert.Equal(0,await s.Db.FootballResults.CountAsync(x=>x.SourceId==s.Source && x.RecordedAtUtc<=cutoff));
        Assert.Equal(first.Hash,(await s.Get<ProviderPublication>().VerifyAsync(first.Report.ReceiptId,false)).Hash);
        Assert.Equal(2,await s.Db.FootballResults.CountAsync(x=>x.SourceId==s.Source));
        var cohort = await s.Get<FootballProspectiveReports>().BuildAsync(new(1,s.Source,[correction.Report.ExecutionId],[],[]),default);
        Assert.Equal(0,cohort.FrozenPredictions); Assert.Equal(0,cohort.EligiblePredictions);
        Assert.Contains("independent_coverage_evidence_required",cohort.Coverage.Single().Reasons);
        var jobs=s.Get<PostgreSqlPipeline>(); var cohortJob=Guid.NewGuid();
        var cohortInput=new ProspectiveCohortInput(1,s.Source,[correction.Report.ExecutionId],[],[]);
        await jobs.PlanAsync(cohortJob,new(1,PipelineKind.ProspectiveCohort,new(await s.Now()),JsonSerializer.Serialize(cohortInput)),Approval);
        await jobs.SetEnabledAsync(cohortJob,true,Approval); var cohortClaim=(await jobs.AcquireAsync(Approval))!;
        var work=await s.Get<PipelineWork>().ExecuteAsync(cohortClaim,Approval,default);
        await jobs.CompleteAsync(cohortClaim,work.Outcome,Approval,artifact:work.Artifact);
        var monitor=await f.Activation.StatusAsync(s.Source,true);
        Assert.NotNull(monitor.Cohort); Assert.Equal(0,monitor.Cohort.Report.EligiblePredictions);
        Assert.Equal("BlockedByExistingEvidenceGates",monitor.PredictionReadiness); Assert.Equal(3,f.Handler.Calls);
    }
    [Fact] public async Task Activation_get_is_loopback_read_only_production_and_revocation_protected()
    {
        var f = await Create(); await using var s = f.S; await Activate(f);
        var before = await s.Db.Set<ProviderActivationEvent>().CountAsync();
        var path = "/api/v1/dashboard/provider-activation/"+s.Source;
        await using var host = new ProviderPublicationWorkflowTests.ApiHost(fixture.GetConnectionString(),s.Root);
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Equal("no-store",response.Headers.CacheControl!.ToString()); Assert.DoesNotContain("synthetic-secret",await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.MethodNotAllowed,(await client.PostAsync(path,null)).StatusCode);
        Assert.Equal(before,await s.Db.Set<ProviderActivationEvent>().CountAsync()); Assert.Equal(0,f.Handler.Calls);
        await using var prod = new ProviderPublicationWorkflowTests.ApiHost(fixture.GetConnectionString(),s.Root,"Production");
        using var production = prod.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Forbidden,(await production.GetAsync(path)).StatusCode);
        var p = await s.Db.SourcePolicies.Include(x=>x.Audit).SingleAsync(x=>x.Id==f.Grant.PolicyId);
        p.Revoke(Guid.NewGuid(),Approval.Actor,Approval.Reason,await s.Now()); await s.Db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(path)).StatusCode); Assert.Equal(0,f.Handler.Calls);
    }
    [Theory] [InlineData("lease")] [InlineData("rights")] [InlineData("quota")]
    public async Task Deferred_database_fence_rejects_invalid_reservation_at_commit(string fault)
    {
        var f = await Create(budget:1); await using var s = f.S; var input = await Activate(f);
        var first = await Claim(f,input); await Execute(f,first,input); var second = await Claim(f,input);
        var original = await s.Db.Set<ProviderRequestEvent>().AsNoTracking().SingleAsync(x=>x.ExecutionId==first.ExecutionId && x.Stage=="Reserved");
        await using var tx = await s.Db.Database.BeginTransactionAsync();
        s.Db.Add(new ProviderRequestEvent { Id=Guid.NewGuid(),RequestId=Guid.NewGuid(),SourceId=s.Source,GrantId=f.Grant.Id,ExecutionId=second.ExecutionId,
            Owner=second.Owner,Attempt=second.Attempt,RunId=original.RunId,Stage="Reserved",Category="controlled_direct_sql_fence_test" });
        await s.Db.SaveChangesAsync();
        if (fault=="lease") await s.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE pipeline.\"Executions\" SET \"LeaseUntilUtc\"=clock_timestamp() WHERE \"Id\"={second.ExecutionId}");
        if (fault=="rights")
        {
            var p=await s.Db.SourcePolicies.Include(x=>x.Audit).SingleAsync(x=>x.Id==f.Grant.PolicyId);
            p.Revoke(Guid.NewGuid(),Approval.Actor,Approval.Reason,await s.Now()); await s.Db.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<PostgresException>(()=>tx.CommitAsync());
        Assert.Equal("23514", error.SqlState);
        Assert.Equal(fault switch { "lease" => "Provider request owner fenced at commit", "rights" => "Provider activation rights unavailable at commit", _ => "Provider durable quota unavailable" }, error.MessageText);
    }
}
