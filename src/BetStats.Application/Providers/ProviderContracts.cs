using BetStats.Application.Governance;
using BetStats.Domain.Governance;

namespace BetStats.Application.Providers;

public enum ProviderCapability { MetadataDiscovery, EventMetadata, HistoricalObservations }
public enum ProviderErrorCategory { AuthenticationFailure, RateLimitExceeded, TemporaryUnavailability, InvalidResponse, PermissionDenied, UnsupportedCapability, InvalidConfiguration, BudgetExhausted, Timeout }
public sealed record ProviderError(ProviderErrorCategory Category, string Code, TimeSpan? RetryAfter = null);
public sealed record ProviderResult(bool Success, ProviderError? Error = null);
public sealed record ProviderRequest(Guid SportId, ProviderCapability Capability, UsageContext Context);
public sealed record ConfigurationValidation(bool Valid, IReadOnlyList<string> ErrorCodes);

public sealed class ProviderDescriptor
{
    public ProviderDescriptor(Guid dataSourceId, string code, IEnumerable<Guid> sports, IEnumerable<ProviderCapability> capabilities)
    {
        if (dataSourceId == Guid.Empty) throw new ArgumentException("Source UUID is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(sports); ArgumentNullException.ThrowIfNull(capabilities);
        var sportList = sports.Distinct().ToArray(); var capabilityList = capabilities.Distinct().ToArray();
        if (sportList.Length == 0 || sportList.Contains(Guid.Empty) || capabilityList.Length == 0 || capabilityList.Any(c => !Enum.IsDefined(c)))
            throw new ArgumentException("At least one valid sport and capability are required.");
        DataSourceId = dataSourceId; Code = code;
        Sports = Array.AsReadOnly(sportList); Capabilities = Array.AsReadOnly(capabilityList);
    }
    public Guid DataSourceId { get; }
    public string Code { get; }
    public IReadOnlyList<Guid> Sports { get; }
    public IReadOnlyList<ProviderCapability> Capabilities { get; }
}

// Future adapters must honor cancellation and classify failures without leaking credentials/responses.
public interface IProviderAdapter
{
    ProviderDescriptor Descriptor { get; }
    ConfigurationValidation ValidateConfiguration();
    Task<ProviderResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken);
}

public sealed class AuthorizedProviderExecutor(ISourcePolicyEvaluator evaluator, TimeProvider clock, ISourceOperationalStatus sourceStatus)
{
    public async Task<ProviderResult> ExecuteAsync(IProviderAdapter provider, ProviderRequest request, RequestBudget budget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider); ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();
        static ProviderResult Failure(ProviderErrorCategory category, string code) => new(false, new(category, code));
        ProviderDescriptor descriptor;
        try
        {
            descriptor = provider.Descriptor;
            if (descriptor is null) return Failure(ProviderErrorCategory.InvalidConfiguration, "invalid_configuration");
            if (!descriptor.Sports.Contains(request.SportId) || !descriptor.Capabilities.Contains(request.Capability))
                return Failure(ProviderErrorCategory.UnsupportedCapability, "unsupported_capability");
            if (provider.ValidateConfiguration() is not { Valid: true }) return Failure(ProviderErrorCategory.InvalidConfiguration, "invalid_configuration");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ProviderErrorCategory.InvalidConfiguration, "invalid_configuration");
        }
        var initialStatus = await sourceStatus.ReadAsync(descriptor.DataSourceId, cancellationToken);
        if (initialStatus != SourceOperationalStatus.Enabled)
            return Failure(ProviderErrorCategory.PermissionDenied, initialStatus == SourceOperationalStatus.Missing ? "source_missing" : "source_disabled");
        var purpose = request.Capability == ProviderCapability.MetadataDiscovery ? DataPurpose.MetadataDiscovery : DataPurpose.DataRetrieval;
        var now = clock.GetUtcNow().UtcDateTime;
        now = now.AddTicks(-(now.Ticks % 10));
        var authorization = await evaluator.EvaluateAsync(descriptor.DataSourceId, purpose, now, request.Context, cancellationToken);
        if (!authorization.Allowed) return Failure(ProviderErrorCategory.PermissionDenied, "policy_" + authorization.Reason);
        var status = await sourceStatus.ReadAsync(descriptor.DataSourceId, cancellationToken);
        if (status != SourceOperationalStatus.Enabled)
            return Failure(ProviderErrorCategory.PermissionDenied, status == SourceOperationalStatus.Missing ? "source_missing" : "source_disabled");
        cancellationToken.ThrowIfCancellationRequested();
        var lease = budget.TryAcquire();
        if (lease is null) return Failure(ProviderErrorCategory.BudgetExhausted, "request_budget_exhausted");
        using var timeout = new CancellationTokenSource(budget.Configuration.Timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        Task<ProviderResult>? operation = null;
        try
        {
            operation = provider.ExecuteAsync(request, linked.Token);
            var result = await operation.WaitAsync(linked.Token);
            if (result is null || result.Success == (result.Error is not null)) return Failure(ProviderErrorCategory.InvalidResponse, "inconsistent_result");
            if (result.Error is { } error && (!Enum.IsDefined(error.Category) || string.IsNullOrWhiteSpace(error.Code)))
                return Failure(ProviderErrorCategory.InvalidResponse, "invalid_error");
            if (result.Error?.RetryAfter is { } delay && (delay < TimeSpan.Zero || delay > DateTimeOffset.MaxValue - clock.GetUtcNow() || result.Error.Category != ProviderErrorCategory.RateLimitExceeded))
                return Failure(ProviderErrorCategory.InvalidResponse, "invalid_retry_after");
            if (result.Error is { Category: ProviderErrorCategory.RateLimitExceeded, RetryAfter: { } retryAfter })
            {
                budget.ApplyRetryAfter(retryAfter);
            }
            return result; // No automatic retries, regardless of error category.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return Failure(ProviderErrorCategory.Timeout, "provider_timeout");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ProviderErrorCategory.TemporaryUnavailability, "provider_execution_failed");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        finally
        {
            if (operation is null || operation.IsCompleted) lease.Dispose();
            else
                // An adapter ignoring cancellation cannot free concurrency while still running.
                _ = operation.ContinueWith(task => { _ = task.Exception; lease.Dispose(); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
