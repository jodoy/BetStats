using BetStats.Domain.Identity;

namespace BetStats.Application.Identity;

public interface IIdentityResolutionHistory
{
    Task<IdentityResolution?> ReadLatestAsOfAsync(Guid providerIdentityId, DateTime asOfUtc, CancellationToken cancellationToken = default);
    // Version uniqueness supplies optimistic concurrency: two writers appending
    // the same next version cannot both commit. No candidate selection is automatic.
    Task AppendAsync(IdentityResolution decision, CancellationToken cancellationToken = default);
}
