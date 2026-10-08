namespace BetStats.Application.Providers;

public sealed record RequestBudgetConfiguration
{
    public RequestBudgetConfiguration(int requestsPerMinute, int requestsPerDay, int maximumConcurrency, TimeSpan timeout)
    {
        if (requestsPerMinute <= 0 || requestsPerDay <= 0 || maximumConcurrency <= 0 || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentException("Request counts/concurrency must be positive; timeout must be (0, 5 minutes].");
        RequestsPerMinute = requestsPerMinute; RequestsPerDay = requestsPerDay; MaximumConcurrency = maximumConcurrency; Timeout = timeout;
    }
    public int RequestsPerMinute { get; }
    public int RequestsPerDay { get; }
    public int MaximumConcurrency { get; }
    public TimeSpan Timeout { get; }
}

// One instance per provider per process, shared by its callers. Not a distributed quota.
public sealed class RequestBudget(RequestBudgetConfiguration configuration, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Queue<long> minute = new();
    private readonly Queue<long> day = new();
    private DateTimeOffset cooldownUntil = DateTimeOffset.MinValue;
    private int concurrent;
    public RequestBudgetConfiguration Configuration { get; } = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public IDisposable? TryAcquire()
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            while (minute.TryPeek(out var oldest) && clock.GetElapsedTime(oldest, now) >= TimeSpan.FromMinutes(1)) minute.Dequeue();
            while (day.TryPeek(out var oldest) && clock.GetElapsedTime(oldest, now) >= TimeSpan.FromDays(1)) day.Dequeue();
            if (clock.GetUtcNow() < cooldownUntil || minute.Count >= Configuration.RequestsPerMinute || day.Count >= Configuration.RequestsPerDay || concurrent >= Configuration.MaximumConcurrency)
                return null;
            minute.Enqueue(now); day.Enqueue(now); concurrent++;
            return new Lease(this);
        }
    }
    public void ApplyRetryAfter(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero) throw new ArgumentException("Retry-After delay must not be negative.");
        var now = clock.GetUtcNow();
        ApplyRetryAfter(delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay);
    }
    public void ApplyRetryAfter(DateTimeOffset retryAt)
    {
        lock (gate) { if (retryAt > cooldownUntil) cooldownUntil = retryAt; }
    }
    private sealed class Lease(RequestBudget owner) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) lock (owner.gate) owner.concurrent--;
        }
    }
}
