using BetStats.Application.Identity;
using BetStats.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Persistence;

public sealed class IdentityResolutionHistory(BetStatsDbContext context) : IIdentityResolutionHistory
{
    public Task<IdentityResolution?> ReadLatestAsOfAsync(Guid providerIdentityId, DateTime asOfUtc, CancellationToken cancellationToken = default)
    {
        if (providerIdentityId == Guid.Empty || asOfUtc.Kind != DateTimeKind.Utc || asOfUtc.Ticks % 10 != 0)
            throw new ArgumentException("A nonempty identity UUID and UTC cutoff at microsecond precision are required.");
        return context.IdentityResolutions.AsNoTracking().Where(item => item.ProviderIdentityId == providerIdentityId && item.DecidedAtUtc <= asOfUtc)
            .OrderByDescending(item => item.Version).FirstOrDefaultAsync(cancellationToken);
    }
    public async Task AppendAsync(IdentityResolution decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        context.IdentityResolutions.Add(decision);
        await context.SaveChangesAsync(cancellationToken);
    }
}
