using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Domain.Governance;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Ingestion;

namespace BetStats.IntegrationTests;

// Controlled fake transport only. No provider URLs or HTTP client are used.
[Trait("Category", "Security")]
public sealed class AcquisitionProviderTests
{
    private sealed class Policy : ISourcePolicyEvaluator
    {
        public Guid Id { get; } = Guid.NewGuid(); public bool Allowed { get; set; } = true;
        public Task<PolicyEvaluation> EvaluateAsync(Guid source, DataPurpose purpose, DateTime at, UsageContext context, CancellationToken token = default) =>
            Task.FromResult(new PolicyEvaluation(Allowed, Allowed ? PolicyReason.Authorized : PolicyReason.Revoked, Id, 1, at, []));
    }
    private sealed class Status : ISourceOperationalStatus
    { public Task<SourceOperationalStatus> ReadAsync(Guid source, CancellationToken token = default) => Task.FromResult(SourceOperationalStatus.Enabled); }
    private sealed class Transport(Func<CancellationToken, Task<FootballApiPage>> next) : IFootballApiTransport
    {
        public int Calls { get; private set; }
        public FootballApiPageRequest? LastRequest { get; private set; }
        public Task<FootballApiPage> ReadPageAsync(FootballApiPageRequest request, CancellationToken token) { LastRequest = request; Calls++; return next(token); }
    }
    private static AcquisitionCapability Capability(Policy p) => new(1, Guid.NewGuid(), "controlled-fixture", p.Id, 1, FootballTransport.Api,
        "football-history-v1", [AcquisitionCategory.PrematchMetadata], MaximumPages: 2);
    private static readonly ProviderRequest Request = new(ReferenceSports.All.Single(s => s.Code == "football").Id, ProviderCapability.HistoricalObservations, new());
    [Theory] [InlineData("policy")] [InlineData("capability")] [InlineData("configuration")]
    public async Task Invalid_or_revoked_configuration_never_calls_transport(string fault)
    {
        var p = new Policy(); var c = Capability(p); var t = new Transport(_ => Task.FromResult(new FootballApiPage(new byte[] { 1 }, null)));
        if (fault == "policy") p.Allowed = false;
        if (fault == "capability") c = c with { Categories = [AcquisitionCategory.HistoricalResults] };
        if (fault == "configuration") c = c with { PolicyVersion = 0 };
        var a = new GovernedFootballPageAdapter(c, new("FICT", "2026"), AcquisitionCategory.PrematchMetadata, t, new(null, 10), p, TimeProvider.System);
        var result = await new AuthorizedProviderExecutor(p, TimeProvider.System, new Status()).ExecuteAsync(a, Request, new(new(10, 10, 1, TimeSpan.FromSeconds(1)), TimeProvider.System));
        Assert.False(result.Success); Assert.Equal(0, t.Calls); Assert.Null(a.Content);
    }
    [Fact] public async Task Controlled_429_sets_cooldown_without_retry_or_diagnostic_payload_leak()
    {
        var p = new Policy(); var t = new Transport(_ => Task.FromResult(new FootballApiPage(default, null, new(ProviderErrorCategory.RateLimitExceeded, "secret-provider-body", TimeSpan.FromMinutes(1)))));
        var budget = new RequestBudget(new(10, 10, 1, TimeSpan.FromSeconds(1)), TimeProvider.System);
        var a = new GovernedFootballPageAdapter(Capability(p), new("FICT", "2026"), AcquisitionCategory.PrematchMetadata, t, new(null, 10), p, TimeProvider.System);
        var executor = new AuthorizedProviderExecutor(p, TimeProvider.System, new Status());
        var first = await executor.ExecuteAsync(a, Request, budget); Assert.Equal(ProviderErrorCategory.RateLimitExceeded, first.Error!.Category);
        Assert.DoesNotContain("secret", first.Error.Code); Assert.Equal(ProviderErrorCategory.BudgetExhausted, (await executor.ExecuteAsync(a, Request, budget)).Error!.Category); Assert.Equal(1, t.Calls);
    }
    [Fact] public async Task Controlled_timeout_is_bounded_and_never_retried()
    {
        var p = new Policy(); var t = new Transport(async token => { await Task.Delay(Timeout.Infinite, token); return new(default, null); });
        var a = new GovernedFootballPageAdapter(Capability(p), new("FICT", "2026"), AcquisitionCategory.PrematchMetadata, t, new(null, 10), p, TimeProvider.System);
        var result = await new AuthorizedProviderExecutor(p, TimeProvider.System, new Status()).ExecuteAsync(a, Request, new(new(10, 10, 1, TimeSpan.FromMilliseconds(100)), TimeProvider.System));
        Assert.Equal(ProviderErrorCategory.Timeout, result.Error!.Category); Assert.Equal(1, t.Calls);
    }
    [Theory] [InlineData(0)] [InlineData(1048577)]
    public async Task Malformed_or_oversize_response_cannot_become_content(int size)
    {
        var p = new Policy(); var t = new Transport(_ => Task.FromResult(new FootballApiPage(new byte[size], null)));
        var a = new GovernedFootballPageAdapter(Capability(p), new("FICT", "2026"), AcquisitionCategory.PrematchMetadata, t, new(null, 10), p, TimeProvider.System);
        var r = await new AuthorizedProviderExecutor(p, TimeProvider.System, new Status()).ExecuteAsync(a, Request, new(new(10, 10, 1, TimeSpan.FromSeconds(1)), TimeProvider.System));
        Assert.Equal(ProviderErrorCategory.InvalidResponse, r.Error!.Category); Assert.Null(a.Content);
    }
    [Fact] public void Existing_pagination_rejects_repeated_cursor_and_bound_exhaustion()
    {
        var p = new FootballPagination(2, 10); _ = p.Next(null); _ = p.Next("next");
        Assert.Throws<InvalidOperationException>(() => p.Next("next"));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Every_page_is_scoped_budgeted_and_pagination_is_bounded(bool exhaustBudget)
    {
        var p = new Policy(); var c = Capability(p); var scope = new FootballImportScope("FICT", "2026");
        var t = new Transport(_ => Task.FromResult(new FootballApiPage(new byte[] { 1 }, "next")));
        var budget = new RequestBudget(new(exhaustBudget ? 1 : 10, 10, 1, TimeSpan.FromSeconds(1)), TimeProvider.System);
        var r = await GovernedFootballPages.ReadAsync(c, scope, AcquisitionCategory.PrematchMetadata, t, p, TimeProvider.System,
            new(p, TimeProvider.System, new Status()), budget);
        Assert.False(r.Result.Success); Assert.Equal(exhaustBudget ? ProviderErrorCategory.BudgetExhausted : ProviderErrorCategory.InvalidResponse, r.Result.Error!.Category);
        Assert.Equal(exhaustBudget ? 1 : 2, t.Calls); Assert.Equal(scope, t.LastRequest!.Acquisition!.Scope);
        Assert.Equal(c.PolicyId, t.LastRequest.Acquisition.PolicyId); Assert.Equal(AcquisitionCategory.PrematchMetadata, t.LastRequest.Acquisition.Category);
    }
    [Fact] public async Task Revocation_during_controlled_response_prevents_content_publication()
    {
        var p = new Policy(); var t = new Transport(_ => { p.Allowed = false; return Task.FromResult(new FootballApiPage(new byte[] { 1 }, null)); });
        var a = new GovernedFootballPageAdapter(Capability(p), new("FICT", "2026"), AcquisitionCategory.PrematchMetadata, t, new(null, 10), p, TimeProvider.System);
        var r = await new AuthorizedProviderExecutor(p, TimeProvider.System, new Status()).ExecuteAsync(a, Request, new(new(10, 10, 1, TimeSpan.FromSeconds(1)), TimeProvider.System));
        Assert.Equal(ProviderErrorCategory.PermissionDenied, r.Error!.Category); Assert.Null(a.Content);
    }
}
