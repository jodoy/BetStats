using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Domain.Governance;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Ingestion;
using BetStats.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BetStats.IntegrationTests;

// All HTTP responses below are project-owned fictional payloads. Never a real call.
public sealed class ApiFootballBoundaryTests
{
    private static readonly ApiFootballCanary Scope = new(1, 999999, 2026, new(2026, 10, 13), new(2026, 10, 19));
    private static readonly ProviderRequest Request = new(ReferenceSports.All.Single(s => s.Code == "football").Id, ProviderCapability.HistoricalObservations, new());
    private const string Secret = "fictional-test-key";
    private sealed class Policy : ISourcePolicyEvaluator
    {
        public Guid Id { get; } = Guid.NewGuid(); public bool Allowed { get; set; } = true;
        public Task<PolicyEvaluation> EvaluateAsync(Guid source, DataPurpose purpose, DateTime at, UsageContext context, CancellationToken token = default) =>
            Task.FromResult(new PolicyEvaluation(Allowed, Allowed ? PolicyReason.Authorized : PolicyReason.Revoked, Id, 1, at, []));
    }
    private sealed class Status : ISourceOperationalStatus
    { public Task<SourceOperationalStatus> ReadAsync(Guid source, CancellationToken token = default) => Task.FromResult(SourceOperationalStatus.Enabled); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return response(request, token); }
    }
    private static HttpResponseMessage Response(string json, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static ApiFootballFixtureAdapter Adapter(Handler handler, Policy p, string? key = Secret, bool development = true,
        Func<bool>? enabled = null, AcquisitionPageContext? context = null) => new(new HttpMessageInvoker(handler), key, development,
            enabled ?? (() => true), context ?? new(1, Guid.NewGuid(), p.Id, 1, new("999999", "2026"), AcquisitionCategory.PrematchMetadata), Scope, p, TimeProvider.System);
    private static Task<ProviderResult> Execute(ApiFootballFixtureAdapter a, Policy p, RequestBudget? budget = null, ProviderRequest? request = null) =>
        new AuthorizedProviderExecutor(p, TimeProvider.System, new Status()).ExecuteAsync(a, request ?? Request,
            budget ?? new(new(10, 10, 1, TimeSpan.FromSeconds(1)), TimeProvider.System));
    private static string Payload(string status = "NS")
    {
        var final = status == "FT";
        return System.Text.Json.JsonSerializer.Serialize(new {
            get = "fixtures", errors = Array.Empty<string>(), results = 1, paging = new { current = 1, total = 1 },
            response = new[] { new {
                fixture = new { id = 999001, timezone = "UTC", date = "2026-10-15T18:00:00+00:00", timestamp = new DateTimeOffset(2026, 10, 15, 18, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), status = new { @short = status } },
                league = new { id = 999999, season = 2026 }, teams = new { home = new { id = 99901 }, away = new { id = 99902 } },
                goals = new { home = final ? (int?)2 : null, away = final ? (int?)1 : null },
                score = new { fulltime = new { home = final ? (int?)2 : null, away = final ? (int?)1 : null }, halftime = new { home = final ? (int?)1 : null, away = final ? (int?)0 : null } }
            } }
        });
    }
    private static IReadOnlyList<ApiFootballFixture> Read(string json) => ApiFootballFixtureReader.Read(Encoding.UTF8.GetBytes(json), Scope);

    [Theory] [InlineData("missing")] [InlineData("bad-key")] [InlineData("production")] [InlineData("disabled")]
    [InlineData("revoked")] [InlineData("scope")] [InlineData("policy-version")] [InlineData("capability")]
    public async Task Blockers_prevent_network_and_startup_is_inert(string fault)
    {
        var p = new Policy(); var handler = new Handler((_, _) => Task.FromResult(Response(Payload())));
        var context = new AcquisitionPageContext(1, Guid.NewGuid(), p.Id, fault == "policy-version" ? 2 : 1,
            new(fault == "scope" ? "1" : "999999", "2026"), AcquisitionCategory.PrematchMetadata);
        if (fault == "revoked") p.Allowed = false;
        var a = Adapter(handler, p, fault == "missing" ? null : fault == "bad-key" ? "header\ninjection" : Secret,
            fault != "production", () => fault != "disabled", context);
        Assert.Equal(0, handler.Calls);
        Assert.False((await Execute(a, p, request: fault == "capability" ? Request with { Capability = ProviderCapability.EventMetadata } : Request)).Success);
        Assert.Equal(0, handler.Calls); Assert.Null(a.Content);
        Assert.DoesNotContain(Secret, a.ToString());
    }
    [Fact] public async Task Exact_fixed_endpoint_scope_header_and_original_json_are_preserved()
    {
        var p = new Policy(); var original = Payload();
        var handler = new Handler((message, _) => {
            Assert.Equal("https://v3.football.api-sports.io/fixtures?league=999999&season=2026&from=2026-10-13&to=2026-10-19&timezone=UTC", message.RequestUri!.AbsoluteUri);
            Assert.Equal(Secret, message.Headers.GetValues("x-apisports-key").Single()); Assert.Equal(HttpMethod.Get, message.Method);
            return Task.FromResult(Response(original)); });
        var a = Adapter(handler, p); Assert.True((await Execute(a, p)).Success);
        Assert.Equal(original, Encoding.UTF8.GetString(a.Content!.Bytes.Span)); Assert.Equal("application/json", a.Content.ContentType);
        Assert.Equal(ApiFootballFixtureAdapter.Profile, a.Content.ParserVersion); Assert.Null(Read(original).Single().FullHome);
    }
    [Theory] [InlineData(401, ProviderErrorCategory.AuthenticationFailure)] [InlineData(403, ProviderErrorCategory.AuthenticationFailure)]
    [InlineData(302, ProviderErrorCategory.InvalidResponse)] [InlineData(500, ProviderErrorCategory.TemporaryUnavailability)]
    [InlineData(499, ProviderErrorCategory.TemporaryUnavailability)] [InlineData(204, ProviderErrorCategory.InvalidResponse)]
    public async Task Errors_are_sanitized_without_implicit_retries(int status, ProviderErrorCategory category)
    {
        var p = new Policy(); var h = new Handler((_, _) => Task.FromResult(Response(Secret, status))); var a = Adapter(h, p);
        var r = await Execute(a, p); Assert.Equal(category, r.Error!.Category); Assert.DoesNotContain(Secret, r.Error.Code);
        Assert.Equal(1, h.Calls); Assert.Null(a.Content);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Retry_after_delta_or_http_date_enters_existing_budget_cooldown(bool date)
    {
        var p = new Policy(); var h = new Handler((_, _) => { var r = Response(Secret, 429);
            r.Headers.RetryAfter = date ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(2)) : new RetryConditionHeaderValue(TimeSpan.FromMinutes(2)); return Task.FromResult(r); });
        var a = Adapter(h, p); var b = new RequestBudget(new(10, 10, 1, TimeSpan.FromSeconds(1)), TimeProvider.System);
        Assert.Equal(ProviderErrorCategory.RateLimitExceeded, (await Execute(a, p, b)).Error!.Category);
        Assert.Equal(ProviderErrorCategory.BudgetExhausted, (await Execute(a, p, b)).Error!.Category); Assert.Equal(1, h.Calls);
    }
    [Fact] public async Task Timeout_is_bounded_by_existing_executor()
    {
        var p = new Policy(); var h = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Response(Secret); });
        Assert.Equal(ProviderErrorCategory.Timeout, (await Execute(Adapter(h, p), p, new(new(1, 1, 1, TimeSpan.FromMilliseconds(50)), TimeProvider.System))).Error!.Category); Assert.Equal(1, h.Calls);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Inflight_revocation_or_disable_prevents_content_publication(bool disable)
    {
        var p = new Policy(); var enabled = true; var h = new Handler((_, _) => { if (disable) enabled = false; else p.Allowed = false; return Task.FromResult(Response(Payload())); });
        var a = Adapter(h, p, enabled: () => enabled); Assert.Equal(ProviderErrorCategory.PermissionDenied, (await Execute(a, p)).Error!.Category); Assert.Null(a.Content);
    }
    [Theory] [InlineData(0)] [InlineData(1048577)]
    public async Task Empty_or_oversize_body_is_rejected(int size)
    {
        var p = new Policy(); var h = new Handler((_, _) => Task.FromResult(Response(new string('x', size))));
        var a = Adapter(h, p); Assert.Equal(ProviderErrorCategory.InvalidResponse, (await Execute(a, p)).Error!.Category); Assert.Null(a.Content);
    }
    [Fact] public async Task Invalid_json_is_preserved_for_raw_capture_before_interpretation()
    {
        var p = new Policy(); var original = "{malformed-fictional-json"; var h = new Handler((_, _) => Task.FromResult(Response(original)));
        var a = Adapter(h, p); Assert.True((await Execute(a, p)).Success); Assert.Equal(original, Encoding.UTF8.GetString(a.Content!.Bytes.Span));
        Assert.Throws<InvalidDataException>(() => Read(original)); // A transport success is never a parse/ingestion success.
    }
    [Fact] public async Task Existing_shared_budget_prevents_concurrent_requests_and_exhaustion()
    {
        var p = new Policy(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new Handler(async (_, token) => { started.SetResult(); await release.Task.WaitAsync(token); return Response(Payload()); });
        var budget = new RequestBudget(new(1, 1, 1, TimeSpan.FromSeconds(5)), TimeProvider.System);
        var first = Execute(Adapter(h, p), p, budget); await started.Task;
        Assert.Equal(ProviderErrorCategory.BudgetExhausted, (await Execute(Adapter(h, p), p, budget)).Error!.Category);
        release.SetResult(); Assert.True((await first).Success);
        Assert.Equal(ProviderErrorCategory.BudgetExhausted, (await Execute(Adapter(h, p), p, budget)).Error!.Category); Assert.Equal(1, h.Calls);
    }
    [Theory] [InlineData("UTC", "2026-10-15T18:00:00+00:00", 0)]
    [InlineData("Unrecognized/Zone", "2026-10-15T18:00:00+00:00", 1)]
    [InlineData("UTC", "2026-10-15", 1)] [InlineData("UTC", "2026-10-15T18:00:00+02:00", 1)]
    public void Kickoff_requires_explicit_utc_seconds_and_matching_unix_time(string zone, string date, int blockers)
    {
        var json = JsonNode.Parse(Payload())!; var fixture = json["response"]![0]!["fixture"]!; fixture["timezone"] = zone; fixture["date"] = date;
        var f = Read(json.ToJsonString()).Single(); Assert.Equal(blockers, f.Blockers.Count); Assert.Equal(blockers == 0, f.KickoffUtc.HasValue);
    }
    [Theory] [InlineData("PST")] [InlineData("CANC")] [InlineData("AET")] [InlineData("PEN")] [InlineData("TBD")] [InlineData("ABD")]
    public void Known_non_standard_status_is_preserved_and_blocks_prediction_and_result_use(string status)
    { var f = Read(Payload(status)).Single(); Assert.Equal(status, f.Status); Assert.Contains("unsupported_prediction_or_result_status", f.Blockers); }
    [Theory] [InlineData("unknown")] [InlineData("pagination")] [InlineData("scope")] [InlineData("duplicate")]
    [InlineData("conflict")] [InlineData("error")] [InlineData("goals")] [InlineData("result-count")] [InlineData("duplicate-json-property")]
    public void Invalid_schema_or_scope_never_silently_maps(string fault)
    {
        var j = JsonNode.Parse(Payload("FT"))!;
        switch (fault) {
            case "unknown": j["response"]![0]!["fixture"]!["status"]!["short"] = "NEW_STATUS"; break;
            case "pagination": j["paging"]!["total"] = 2; break;
            case "scope": j["response"]![0]!["league"]!["id"] = 1; break;
            case "duplicate": case "conflict":
                var duplicate = j["response"]![0]!.DeepClone(); if (fault == "conflict") duplicate["teams"]!["away"]!["id"] = 42;
                j["response"]!.AsArray().Add(duplicate); j["results"] = 2; break;
            case "error": j["errors"] = new JsonObject { ["token"] = Secret }; break;
            case "goals": j["response"]![0]!["goals"]!["home"] = 7; break;
            case "result-count": j["results"] = 2; break;
        }
        var json = fault == "duplicate-json-property" ? j.ToJsonString().Replace("\"results\":1", "\"results\":1,\"results\":1") : j.ToJsonString();
        Assert.Equal("api_football_schema_invalid", Assert.Throws<InvalidDataException>(() => Read(json)).Message);
    }
    [Fact] public void Corrections_are_separate_provider_values_and_missing_scores_never_become_zero()
    {
        var original = Payload("FT"); var first = Read(original).Single(); var j = JsonNode.Parse(original)!;
        j["response"]![0]!["score"]!["fulltime"]!["home"] = 3; j["response"]![0]!["goals"]!["home"] = 3;
        Assert.Equal(3, Read(j.ToJsonString()).Single().FullHome); Assert.Equal(2, first.FullHome); Assert.Equal(original, Payload("FT"));
        j["response"]![0]!["score"]!["fulltime"]!["home"] = null; j["response"]![0]!["goals"]!["home"] = null;
        var missing = Read(j.ToJsonString()).Single(); Assert.Null(missing.FullHome); Assert.Contains("missing_final_result", missing.Blockers);
    }
    [Theory] [InlineData("pl-PL", "Aktywacja live jest zablokowana")] [InlineData("en-GB", "Live activation is blocked")]
    public async Task Readiness_is_bilingual_and_does_not_claim_live_evidence(string language, string label)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<ProviderReadiness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Language"] = language }))).ToHtmlString());
        Assert.Contains(label, WebUtility.HtmlDecode(html)); Assert.Contains("API-Football", html); Assert.DoesNotContain(Secret, html);
    }
}
