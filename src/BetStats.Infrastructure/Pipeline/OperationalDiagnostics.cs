using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace BetStats.Infrastructure.Pipeline;

public static class OperationalDiagnostics
{
    private sealed record Context(ILogger Logger, Guid Job, Guid Execution, Guid? Source, long Started);
    private static readonly AsyncLocal<Context?> Current = new();
    private static readonly Meter Meter = new("BetStats.Operations", "1.0");
    private static readonly Counter<long> Events = Meter.CreateCounter<long>("betstats.operation.events");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("betstats.operation.duration", "ms");
    public static IDisposable Begin(ILogger logger, Guid job, Guid execution, Guid? source = null)
    {
        var previous = Current.Value;
        Current.Value = new(logger, job, execution, source, Stopwatch.GetTimestamp());
        return new Scope(() => Current.Value = previous);
    }
    public static void Record(string category, string outcome, Guid? source = null)
    {
        // Categories/outcomes are code-owned constants, never exception/user text.
        if (category.Length > 80 || outcome.Length > 40 || !category.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ||
            !outcome.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) throw new ArgumentException("Fixed diagnostic codes required.");
        var c = Current.Value;
        var duration = c is null ? 0 : Stopwatch.GetElapsedTime(c.Started).TotalMilliseconds;
        Events.Add(1, new KeyValuePair<string, object?>("category", category), new("outcome", outcome));
        Duration.Record(duration, new KeyValuePair<string, object?>("category", category));
        c?.Logger.LogInformation("Operation {Category} at {Utc} correlation {CorrelationId} job {JobId} execution {ExecutionId} source {SourceId} outcome {Outcome} elapsed_ms {ElapsedMs}",
            category, DateTime.UtcNow, c.Execution, c.Job, c.Execution, source ?? c.Source, outcome, duration);
    }
    private sealed class Scope(Action end) : IDisposable { public void Dispose() => end(); }
}
