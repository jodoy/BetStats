using BetStats.Infrastructure.Pipeline;
using Microsoft.Extensions.Logging;

namespace BetStats.IntegrationTests;

[Trait("Category", "Security")]
public sealed class OperationalDiagnosticsTests
{
    private sealed class Capture : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Assert.Null(exception); Lines.Add(formatter(state, exception)); }
    }
    [Fact]
    public void Correlation_duration_and_source_are_safe_and_context_does_not_leak()
    {
        var logger = new Capture(); var job = Guid.NewGuid(); var execution = Guid.NewGuid(); var source = Guid.NewGuid();
        using (OperationalDiagnostics.Begin(logger, job, execution, source))
        {
            foreach (var category in new[] { "job_started", "heartbeat_failed", "raw_staged", "raw_finalized", "provider_quota_reserved", "provider_attempt_uncertain", "provider_publication_committed", "provider_recovery_requested", "permission_denied", "job_finished" })
                OperationalDiagnostics.Record(category, "test");
            Assert.Throws<ArgumentException>(() => OperationalDiagnostics.Record("x-apisports-key:synthetic-secret", "failed"));
        }
        OperationalDiagnostics.Record("outside_execution", "test"); Assert.Equal(10, logger.Lines.Count);
        Assert.All(logger.Lines, line => {
            Assert.Contains(job.ToString(), line); Assert.Contains(execution.ToString(), line); Assert.Contains(source.ToString(), line);
            Assert.Contains("elapsed_ms", line); Assert.DoesNotContain("synthetic-secret", line); Assert.DoesNotContain("Exception", line);
        });
    }
}
