using BetStats.Application.Governance;
using BetStats.Application.Observations;
using BetStats.Application.Providers;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;

namespace BetStats.UnitTests;

public sealed class GovernanceTests
{
    private static readonly DateTime Time = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Source = Guid.NewGuid();
    private static SourcePolicy Policy(PermissionDecision permission = PermissionDecision.Allowed, int version = 1, DateTime? end = null) =>
        new(Guid.NewGuid(), Source, version, Time, end, "synthetic:terms", "synthetic:evidence", Time, [new(DataPurpose.DataRetrieval, permission)]);
    private sealed class History(params PolicyState[] states) : ISourcePolicyHistory
    {
        public Task<IReadOnlyList<PolicyState>> ReadAtAsync(Guid dataSourceId, DateTime atUtc, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PolicyState>>(states);
    }
    private sealed class EnabledSource : ISourceOperationalStatus
    {
        public Task<SourceOperationalStatus> ReadAsync(Guid dataSourceId, CancellationToken cancellationToken = default) => Task.FromResult(SourceOperationalStatus.Enabled);
    }
    [Fact]
    public async Task Missing_policy_denies_by_default()
    {
        var result = await new SourcePolicyEvaluator(new History()).EvaluateAsync(Source, DataPurpose.DataRetrieval, Time, new());
        Assert.False(result.Allowed); Assert.Equal(PolicyReason.MissingPolicy, result.Reason); Assert.Null(result.PolicyId);
    }
    [Theory]
    [InlineData(PolicyStatus.Draft, PermissionDecision.Allowed, PolicyReason.Draft)]
    [InlineData(PolicyStatus.Revoked, PermissionDecision.Allowed, PolicyReason.Revoked)]
    [InlineData(PolicyStatus.Approved, PermissionDecision.Unknown, PolicyReason.UnknownPermission)]
    [InlineData(PolicyStatus.Approved, PermissionDecision.Denied, PolicyReason.ExplicitDenial)]
    [InlineData(PolicyStatus.Approved, PermissionDecision.Allowed, PolicyReason.Authorized)]
    public async Task State_and_permission_are_independent(PolicyStatus status, PermissionDecision permission, PolicyReason reason)
    {
        var policy = Policy(permission);
        var result = await new SourcePolicyEvaluator(new History(new PolicyState(policy, status))).EvaluateAsync(Source, DataPurpose.DataRetrieval, Time, new());
        Assert.Equal(reason, result.Reason); Assert.Equal(reason == PolicyReason.Authorized, result.Allowed);
        Assert.Equal(policy.Id, result.PolicyId); Assert.Equal(1, result.Version); Assert.Equal(Time, result.EvaluatedAtUtc);
    }
    [Fact]
    public async Task Expired_or_future_intervals_do_not_authorize()
    {
        var policy = Policy(end: Time.AddDays(1));
        var evaluator = new SourcePolicyEvaluator(new History(new PolicyState(policy, PolicyStatus.Approved)));
        Assert.Equal(PolicyReason.NotEffective, (await evaluator.EvaluateAsync(Source, DataPurpose.DataRetrieval, Time.AddDays(1), new())).Reason);
        Assert.False((await evaluator.EvaluateAsync(Source, DataPurpose.DataRetrieval, Time.AddDays(-1), new())).Allowed);
    }
    [Fact]
    public async Task Conflicting_approvals_fail_closed_even_when_both_allow()
    {
        var evaluator = new SourcePolicyEvaluator(new History(new PolicyState(Policy(), PolicyStatus.Approved), new(Policy(version: 2), PolicyStatus.Approved)));
        var result = await evaluator.EvaluateAsync(Source, DataPurpose.DataRetrieval, Time, new());
        Assert.False(result.Allowed); Assert.Equal(PolicyReason.ConflictingPolicies, result.Reason);
    }
    [Theory]
    [InlineData(DataPurpose.PublicDisplay)]
    [InlineData(DataPurpose.CommercialUse)]
    [InlineData(DataPurpose.ModelTraining)]
    [InlineData(DataPurpose.Redistribution)]
    public async Task Retrieval_does_not_imply_output_rights(DataPurpose purpose)
    {
        var evaluator = new SourcePolicyEvaluator(new History(new PolicyState(Policy(), PolicyStatus.Approved)));
        Assert.False((await evaluator.EvaluateAsync(Source, purpose, Time, new())).Allowed);
        var context = new UsageContext(PublicDisplay: purpose == DataPurpose.PublicDisplay, Commercial: purpose == DataPurpose.CommercialUse,
            ModelTraining: purpose == DataPurpose.ModelTraining, Redistribution: purpose == DataPurpose.Redistribution);
        Assert.False((await evaluator.EvaluateAsync(Source, DataPurpose.DataRetrieval, Time, context)).Allowed);
    }
    [Fact]
    public async Task Restrictions_require_explicit_usage_context()
    {
        var policy = new SourcePolicy(Guid.NewGuid(), Source, 1, Time, null, "synthetic:terms", "synthetic:evidence", Time,
            [new(DataPurpose.RawPayloadStorage, PermissionDecision.Allowed, "Synthetic attribution", 7)]);
        var evaluator = new SourcePolicyEvaluator(new History(new PolicyState(policy, PolicyStatus.Approved)));
        Assert.Equal(PolicyReason.RestrictionNotSatisfied, (await evaluator.EvaluateAsync(Source, DataPurpose.RawPayloadStorage, Time, new())).Reason);
        Assert.False((await evaluator.EvaluateAsync(Source, DataPurpose.RawPayloadStorage, Time, new(AttributionProvided: true, IntendedRetentionDays: 8))).Allowed);
        var allowed = await evaluator.EvaluateAsync(Source, DataPurpose.RawPayloadStorage, Time, new(AttributionProvided: true, IntendedRetentionDays: 7));
        Assert.True(allowed.Allowed); Assert.Equal(7, Assert.Single(allowed.Restrictions).MaximumRetentionDays);
    }

    [Theory]
    [InlineData(DataPurpose.MetadataDiscovery)]
    [InlineData(DataPurpose.DataRetrieval)]
    [InlineData(DataPurpose.RawPayloadStorage)]
    [InlineData(DataPurpose.HistoricalRetention)]
    [InlineData(DataPurpose.InternalAnalytics)]
    [InlineData(DataPurpose.PublicDisplay)]
    [InlineData(DataPurpose.ModelTraining)]
    [InlineData(DataPurpose.CommercialUse)]
    [InlineData(DataPurpose.Redistribution)]
    public async Task Each_purpose_can_be_granted_without_granting_any_other(DataPurpose granted)
    {
        var policy = new SourcePolicy(Guid.NewGuid(), Source, 1, Time, null, "synthetic:terms", "synthetic:evidence", Time,
            [new(granted, PermissionDecision.Allowed)]);
        var evaluator = new SourcePolicyEvaluator(new History(new PolicyState(policy, PolicyStatus.Approved)));
        foreach (var purpose in Enum.GetValues<DataPurpose>())
            Assert.Equal(purpose == granted, (await evaluator.EvaluateAsync(Source, purpose, Time, new())).Allowed);
    }
    [Fact]
    public void Policy_versions_and_audit_are_explicit_and_immutable()
    {
        var policy = Policy();
        Assert.Equal(9, policy.Permissions.Count); Assert.Equal(PolicyStatus.Draft, policy.Status);
        Assert.Throws<ArgumentException>(() => policy.Revoke(Guid.NewGuid(), "reviewer", "Synthetic", Time));
        var approval = policy.Approve(Guid.NewGuid(), "reviewer", "Verified synthetic evidence", Time, Time.AddHours(1));
        Assert.Equal(PolicyStatus.Approved, policy.Status);
        Assert.Throws<ArgumentException>(() => policy.Approve(Guid.NewGuid(), "reviewer", "Duplicate", Time, Time));
        var revoked = policy.Revoke(Guid.NewGuid(), "reviewer", "New evidence", Time.AddDays(1));
        Assert.Equal(approval.Id, revoked.PreviousAuditId); Assert.Equal(2, revoked.Sequence);
        Assert.Equal(PolicyStatus.Revoked, policy.Status); Assert.Equal(Time.AddHours(1), policy.ApprovedAtUtc);
        Assert.Throws<ArgumentException>(() => policy.Approve(Guid.NewGuid(), "reviewer", "Cannot revive", Time, Time));
    }
    [Fact]
    public void Policy_rejects_invalid_intervals_versions_permissions_and_utc()
    {
        Assert.Throws<ArgumentException>(() => new SourcePolicy(Guid.NewGuid(), Source, 0, Time, null, "terms", "evidence", Time, []));
        Assert.Throws<ArgumentException>(() => new SourcePolicy(Guid.NewGuid(), Source, 1, Time, Time, "terms", "evidence", Time, []));
        Assert.Throws<ArgumentException>(() => new SourcePolicy(Guid.NewGuid(), Source, 1, Time, null, "terms", "evidence", Time,
            [new(DataPurpose.DataRetrieval, PermissionDecision.Allowed), new(DataPurpose.DataRetrieval, PermissionDecision.Denied)]));
        Assert.Throws<ArgumentException>(() => new PurposePermission(DataPurpose.RawPayloadStorage, PermissionDecision.Allowed, maximumRetentionDays: 0));
        Assert.Throws<ArgumentException>(() => new SourcePolicy(Guid.NewGuid(), Source, 1, DateTime.SpecifyKind(Time, DateTimeKind.Local), null, "terms", "evidence", Time, []));
    }
    [Theory]
    [InlineData(0, 10, 1, 10)]
    [InlineData(10, 0, 1, 10)]
    [InlineData(10, 10, 0, 10)]
    [InlineData(10, 10, 1, 0)]
    [InlineData(10, 10, 1, 301)]
    public void Invalid_budgets_are_rejected(int minute, int day, int concurrency, int timeout) =>
        Assert.Throws<ArgumentException>(() => new RequestBudgetConfiguration(minute, day, concurrency, TimeSpan.FromSeconds(timeout)));

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(Time);
        private readonly List<ManualTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => now.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan span)
        {
            now += span;
            foreach (var timer in timers.ToArray()) timer.FireIfDue();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
        private sealed class ManualTimer(Clock owner, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset due = DateTimeOffset.MaxValue;
            private TimeSpan period;
            public bool Change(TimeSpan dueTime, TimeSpan interval)
            {
                due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now + dueTime; period = interval; return true;
            }
            public void FireIfDue()
            {
                if (owner.now < due) return;
                due = period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now + period;
                callback(state);
            }
            public void Dispose() { due = DateTimeOffset.MaxValue; owner.timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    [Fact]
    public void Budgets_enforce_concurrency_sliding_minute_day_and_retry_after()
    {
        var clock = new Clock(); var budget = new RequestBudget(new(1, 2, 1, TimeSpan.FromSeconds(10)), clock);
        var first = Assert.IsAssignableFrom<IDisposable>(budget.TryAcquire());
        Assert.Null(budget.TryAcquire()); first.Dispose(); first.Dispose();
        Assert.Null(budget.TryAcquire());
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = Assert.IsAssignableFrom<IDisposable>(budget.TryAcquire()); second.Dispose();
        clock.Advance(TimeSpan.FromMinutes(1)); Assert.Null(budget.TryAcquire());
        clock.Advance(TimeSpan.FromDays(1));
        budget.ApplyRetryAfter(TimeSpan.FromMinutes(2)); Assert.Null(budget.TryAcquire());
        clock.Advance(TimeSpan.FromMinutes(2));
        using var third = budget.TryAcquire(); Assert.NotNull(third);
        Assert.Throws<ArgumentException>(() => budget.ApplyRetryAfter(TimeSpan.FromSeconds(-1)));
    }
    [Fact]
    public void Concurrency_and_http_date_cooldown_are_independent_of_request_counts()
    {
        var clock = new Clock(); var budget = new RequestBudget(new(10, 10, 1, TimeSpan.FromSeconds(10)), clock);
        using (var first = budget.TryAcquire()) { Assert.NotNull(first); Assert.Null(budget.TryAcquire()); }
        budget.ApplyRetryAfter(clock.GetUtcNow().AddSeconds(10));
        budget.ApplyRetryAfter(clock.GetUtcNow().AddSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2)); Assert.Null(budget.TryAcquire());
        clock.Advance(TimeSpan.FromSeconds(8)); using var next = budget.TryAcquire(); Assert.NotNull(next);
    }
    private sealed class Adapter : IProviderAdapter
    {
        public static readonly Guid Sport = Guid.NewGuid();
        public ProviderDescriptor Descriptor { get; } = new(Source, "synthetic", [Sport], [ProviderCapability.EventMetadata]);
        public bool Valid { get; init; } = true;
        public int Calls { get; private set; }
        public ProviderResult Result { get; init; } = new(true);
        public ConfigurationValidation ValidateConfiguration() => new(Valid, Valid ? [] : ["synthetic_invalid"]);
        public Task<ProviderResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(Result); }
    }
    [Fact]
    public async Task Provider_gate_denies_before_execution_and_validates_capability_and_config()
    {
        var clock = new Clock(); var budget = new RequestBudget(new(10, 10, 1, TimeSpan.FromSeconds(10)), clock);
        var executor = new AuthorizedProviderExecutor(new SourcePolicyEvaluator(new History()), clock, new EnabledSource());
        var adapter = new Adapter(); var request = new ProviderRequest(Adapter.Sport, ProviderCapability.EventMetadata, new());
        Assert.Equal(ProviderErrorCategory.PermissionDenied, (await executor.ExecuteAsync(adapter, request, budget)).Error!.Category);
        Assert.Equal(0, adapter.Calls);
        Assert.Equal(ProviderErrorCategory.UnsupportedCapability, (await executor.ExecuteAsync(adapter, request with { SportId = Guid.NewGuid() }, budget)).Error!.Category);
        Assert.Equal(ProviderErrorCategory.InvalidConfiguration, (await executor.ExecuteAsync(new Adapter { Valid = false }, request, budget)).Error!.Category);
    }
    [Theory]
    [InlineData(ProviderErrorCategory.AuthenticationFailure)]
    [InlineData(ProviderErrorCategory.PermissionDenied)]
    [InlineData(ProviderErrorCategory.TemporaryUnavailability)]
    public async Task Provider_failures_are_never_automatically_retried(ProviderErrorCategory category)
    {
        var clock = new Clock(); var executor = new AuthorizedProviderExecutor(new SourcePolicyEvaluator(new History(new PolicyState(Policy(), PolicyStatus.Approved))), clock, new EnabledSource());
        var adapter = new Adapter { Result = new(false, new(category, "synthetic_failure")) };
        var result = await executor.ExecuteAsync(adapter, new(Adapter.Sport, ProviderCapability.EventMetadata, new()), new(new(10, 10, 1, TimeSpan.FromSeconds(10)), clock));
        Assert.Equal(category, result.Error!.Category); Assert.Equal(1, adapter.Calls);
    }
    [Fact]
    public async Task Rate_limit_response_establishes_cooldown_and_cancellation_is_honored()
    {
        var clock = new Clock(); var executor = new AuthorizedProviderExecutor(new SourcePolicyEvaluator(new History(new PolicyState(Policy(), PolicyStatus.Approved))), clock, new EnabledSource());
        var adapter = new Adapter { Result = new(false, new(ProviderErrorCategory.RateLimitExceeded, "synthetic_limit", TimeSpan.FromSeconds(30))) };
        var budget = new RequestBudget(new(10, 10, 1, TimeSpan.FromSeconds(10)), clock);
        var request = new ProviderRequest(Adapter.Sport, ProviderCapability.EventMetadata, new());
        await executor.ExecuteAsync(adapter, request, budget);
        Assert.Equal(ProviderErrorCategory.BudgetExhausted, (await executor.ExecuteAsync(adapter, request, budget)).Error!.Category);
        Assert.Equal(1, adapter.Calls);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(adapter, request, budget, cancelled.Token));
    }
    [Fact]
    public void Paging_rejects_invalid_limits_and_cursor_keys()
    {
        var query = new ObservationQuery(CanonicalEntityKind.Sport, Time);
        Assert.Throws<ArgumentException>(() => new ObservationPageOptions(0));
        Assert.Throws<ArgumentException>(() => new ObservationPageOptions(1001));
        Assert.Throws<ArgumentException>(() => new ObservationCursor(query, Time.AddSeconds(1), Time, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new ObservationCursor(query, Time, Time, Guid.Empty));
        Assert.Throws<ArgumentException>(() => new ObservationCursor(query, DateTime.SpecifyKind(Time, DateTimeKind.Unspecified), Time, Guid.NewGuid()));
    }

    private sealed class IgnoringCancellationAdapter : IProviderAdapter
    {
        public ProviderDescriptor Descriptor { get; } = new(Source, "synthetic", [Adapter.Sport], [ProviderCapability.EventMetadata]);
        public readonly TaskCompletionSource<ProviderResult> Completion = new();
        public ConfigurationValidation ValidateConfiguration() => new(true, []);
        public Task<ProviderResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken) => Completion.Task;
    }
    [Fact]
    public async Task Timeout_is_deterministic_and_does_not_release_a_still_running_adapter()
    {
        var clock = new Clock();
        var executor = new AuthorizedProviderExecutor(new SourcePolicyEvaluator(new History(new PolicyState(Policy(), PolicyStatus.Approved))), clock, new EnabledSource());
        var budget = new RequestBudget(new(10, 10, 1, TimeSpan.FromSeconds(10)), clock);
        var adapter = new IgnoringCancellationAdapter();
        var pending = executor.ExecuteAsync(adapter, new(Adapter.Sport, ProviderCapability.EventMetadata, new()), budget);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(ProviderErrorCategory.Timeout, (await pending).Error!.Category);
        Assert.Null(budget.TryAcquire());
        adapter.Completion.SetResult(new(true));
        using var next = budget.TryAcquire(); Assert.NotNull(next);
    }
    [Fact]
    public void Descriptor_rejects_empty_sports_and_unknown_capabilities()
    {
        Assert.Throws<ArgumentException>(() => new ProviderDescriptor(Source, "synthetic", [], [ProviderCapability.EventMetadata]));
        Assert.Throws<ArgumentException>(() => new ProviderDescriptor(Source, "synthetic", [Adapter.Sport], [(ProviderCapability)99]));
    }
}
