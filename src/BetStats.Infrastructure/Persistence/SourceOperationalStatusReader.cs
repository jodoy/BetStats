using BetStats.Application.Providers;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Persistence;

public sealed class SourceOperationalStatusReader(BetStatsDbContext context) : ISourceOperationalStatus
{
    public async Task<SourceOperationalStatus> ReadAsync(Guid dataSourceId, CancellationToken cancellationToken = default)
    {
        var enabled = await context.DataSources.AsNoTracking().Where(source => source.Id == dataSourceId)
            .Select(source => (bool?)source.IsEnabled).SingleOrDefaultAsync(cancellationToken);
        return enabled switch { null => SourceOperationalStatus.Missing, false => SourceOperationalStatus.Disabled, true => SourceOperationalStatus.Enabled };
    }
}
