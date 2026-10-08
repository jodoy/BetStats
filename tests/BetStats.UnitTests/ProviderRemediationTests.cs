using BetStats.Application.Governance;
using BetStats.Application.Providers;
using BetStats.Domain.Governance;

namespace BetStats.UnitTests;

public sealed class ProviderRemediationTests
{
    private static readonly Guid Source = Guid.NewGuid();
    private static readonly Guid Sport = Guid.NewGuid();
    private static readonly ProviderRequest Request = new(Sport, ProviderCapability.EventMetadata, new());
    private sealed class Status(SourceOperationalStatus status) : ISourceOperationalStatus
    {
        public SourceOperationalStatus Value { get; set; } = status;
        public Task<SourceOperationalStatus> ReadAsync(Guid dataSourceId, CancellationToken cancellationToken = default) => Task.FromResult(Value);
    }
    private sealed class Policy : ISourcePolicyEvaluator
    {
        public Task<PolicyEvaluation> EvaluateAsync(Guid dataSourceId, DataPurpose purpose, DateTime evaluationUtc, UsageContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PolicyEvaluation(true, PolicyReason.Authorized, Guid.NewGuid(), 1, evaluationUtc, []));
    }
    private sealed class DisablingStatus : ISourceOperationalStatus
    {
        private int reads;
        public Task<SourceOperationalStatus> ReadAsync(Guid dataSourceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(++reads == 1 ? SourceOperationalStatus.Enabled : SourceOperationalStatus.Disabled);
    }
    private sealed class Adapter(Func<CancellationToken, Task<ProviderResult>> execute) : IProviderAdapter
    {
        public ProviderDescriptor Descriptor { get; } = new(Source, "synthetic", [Sport], [ProviderCapability.EventMetadata]);
        public int Calls { get; private set; }
        public bool ThrowConfiguration { get; init; }
        public ConfigurationValidation ValidateConfiguration() => ThrowConfiguration ? throw new InvalidOperationException("synthetic-private-config") : new(true, []);
        public Task<ProviderResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken) { Calls++; return execute(cancellationToken); }
    }
    private static RequestBudget Budget() => new(new(1, 1, 1, TimeSpan.FromSeconds(10)), TimeProvider.System);

    [Theory]
    [InlineData(SourceOperationalStatus.Missing, "source_missing")]
    [InlineData(SourceOperationalStatus.Disabled, "source_disabled")]
    public async Task Unavailable_sources_do_not_invoke_adapter_or_acquire_budget(SourceOperationalStatus status, string code)
    {
        var adapter = new Adapter(_ => Task.FromResult(new ProviderResult(true)));
        var budget = Budget();
        var result = await new AuthorizedProviderExecutor(new Policy(), TimeProvider.System, new Status(status)).ExecuteAsync(adapter, Request, budget);
        Assert.Equal(ProviderErrorCategory.PermissionDenied, result.Error!.Category); Assert.Equal(code, result.Error.Code);
        Assert.Equal(0, adapter.Calls); using var lease = budget.TryAcquire(); Assert.NotNull(lease);
    }

    [Fact]
    public async Task Source_status_is_read_again_for_each_request()
    {
        var status = new Status(SourceOperationalStatus.Enabled);
        var executor = new AuthorizedProviderExecutor(new Policy(), TimeProvider.System, status);
        var adapter = new Adapter(_ => Task.FromResult(new ProviderResult(true)));
        Assert.True((await executor.ExecuteAsync(adapter, Request, Budget())).Success);
        status.Value = SourceOperationalStatus.Disabled;
        Assert.False((await executor.ExecuteAsync(adapter, Request, Budget())).Success);
        Assert.Equal(1, adapter.Calls);
    }

    [Fact]
    public async Task Disablement_at_execution_boundary_denies_without_budget_acquisition()
    {
        var adapter = new Adapter(_ => Task.FromResult(new ProviderResult(true))); var budget = Budget();
        var result = await new AuthorizedProviderExecutor(new Policy(), TimeProvider.System, new DisablingStatus()).ExecuteAsync(adapter, Request, budget);
        Assert.Equal("source_disabled", result.Error!.Code); Assert.Equal(0, adapter.Calls);
        using var lease = budget.TryAcquire(); Assert.NotNull(lease);
    }

    [Fact]
    public async Task Configuration_exception_is_controlled_before_budget_and_execution()
    {
        var adapter = new Adapter(_ => Task.FromResult(new ProviderResult(true))) { ThrowConfiguration = true }; var budget = Budget();
        var result = await new AuthorizedProviderExecutor(new Policy(), TimeProvider.System, new Status(SourceOperationalStatus.Enabled)).ExecuteAsync(adapter, Request, budget);
        Assert.Equal(ProviderErrorCategory.InvalidConfiguration, result.Error!.Category);
        Assert.Equal("invalid_configuration", result.Error.Code); Assert.Equal(0, adapter.Calls);
        using var lease = budget.TryAcquire(); Assert.NotNull(lease);
    }

    public static TheoryData<ProviderResult?> MalformedResults => new()
    {
        null,
        new(true, new(ProviderErrorCategory.InvalidResponse, "synthetic")),
        new(false),
        new(false, new((ProviderErrorCategory)999, "synthetic")),
        new(false, new(ProviderErrorCategory.AuthenticationFailure, null!)),
        new(false, new(ProviderErrorCategory.AuthenticationFailure, "")),
        new(false, new(ProviderErrorCategory.AuthenticationFailure, "  ")),
        new(false, new(ProviderErrorCategory.RateLimitExceeded, "synthetic", TimeSpan.FromTicks(-1))),
        new(false, new(ProviderErrorCategory.RateLimitExceeded, "synthetic", TimeSpan.MaxValue)),
        new(false, new(ProviderErrorCategory.AuthenticationFailure, "synthetic", TimeSpan.FromSeconds(1)))
    };
    [Theory]
    [MemberData(nameof(MalformedResults))]
    public async Task Malformed_results_are_controlled_and_release_the_lease(ProviderResult? malformed)
    {
        var adapter = new Adapter(_ => Task.FromResult(malformed!));
        var budget = new RequestBudget(new(10, 10, 1, TimeSpan.FromSeconds(10)), TimeProvider.System);
        var result = await new AuthorizedProviderExecutor(new Policy(), TimeProvider.System, new Status(SourceOperationalStatus.Enabled)).ExecuteAsync(adapter, Request, budget);
        Assert.False(result.Success); Assert.Equal(ProviderErrorCategory.InvalidResponse, result.Error!.Category);
        Assert.Equal(1, adapter.Calls); using var lease = budget.TryAcquire(); Assert.NotNull(lease);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unexpected_exception_is_classified_without_exposing_exception_text(bool asynchronous)
    {
        var adapter = new Adapter(_ => asynchronous ? Task.FromException<ProviderResult>(new InvalidOperationException("synthetic-private-response")) : throw new InvalidOperationException("synthetic-private-response"));
        var result = await new AuthorizedProviderExecutor(new Policy(), TimeProvider.System, new Status(SourceOperationalStatus.Enabled)).ExecuteAsync(adapter, Request, Budget());
        Assert.Equal(ProviderErrorCategory.TemporaryUnavailability, result.Error!.Category);
        Assert.Equal("provider_execution_failed", result.Error.Code); Assert.Equal(1, adapter.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_during_execution_propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new Adapter(async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(true); });
        var pending = new AuthorizedProviderExecutor(new Policy(), TimeProvider.System, new Status(SourceOperationalStatus.Enabled)).ExecuteAsync(adapter, Request, Budget(), cancellation.Token);
        await entered.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
