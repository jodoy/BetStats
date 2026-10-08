namespace BetStats.Application.Providers;

public enum SourceOperationalStatus { Missing, Disabled, Enabled }

public interface ISourceOperationalStatus
{
    Task<SourceOperationalStatus> ReadAsync(Guid dataSourceId, CancellationToken cancellationToken = default);
}
