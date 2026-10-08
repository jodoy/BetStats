using BetStats.Application.Governance;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Persistence;

public sealed class SourcePolicyHistory(BetStatsDbContext context) : ISourcePolicyHistory
{
    public async Task<IReadOnlyList<PolicyState>> ReadAtAsync(Guid dataSourceId, DateTime atUtc, CancellationToken cancellationToken = default)
    {
        if (dataSourceId == Guid.Empty || atUtc.Kind != DateTimeKind.Utc || atUtc.Ticks % 10 != 0) throw new ArgumentException("Source UUID and UTC cutoff required.");
        var policies = await context.SourcePolicies.AsNoTracking().Where(p => p.DataSourceId == dataSourceId && p.RecordedAtUtc <= atUtc)
            .Include(p => p.Permissions).Include(p => p.Audit.Where(a => a.RecordedAtUtc <= atUtc &&
                (a.ApprovedAtUtc == null || a.ApprovedAtUtc <= atUtc))).AsSplitQuery().ToListAsync(cancellationToken);
        return policies.Select(p => new PolicyState(p, p.Status)).ToArray();
    }
}
