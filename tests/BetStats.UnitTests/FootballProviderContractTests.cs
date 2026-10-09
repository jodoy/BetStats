using BetStats.Application.Providers;

namespace BetStats.UnitTests;

public sealed class FootballProviderContractTests
{
    [Fact]
    public void Capability_is_versioned_bounded_and_grants_no_inventory_permission()
    {
        var capability = FootballProviderContract.LocalHistory;
        Assert.True(capability.IsValid); Assert.Equal(1, capability.Version);
        Assert.Equal(FootballTransport.LocalCsv, capability.Transport); Assert.False(capability.SupportsInventory);
        Assert.False((capability with { Version = 2 }).IsValid);
        Assert.False((capability with { MaximumBytes = int.MaxValue }).IsValid);
    }
    [Theory]
    [InlineData(ProviderErrorCategory.RateLimitExceeded, true)]
    [InlineData(ProviderErrorCategory.Timeout, true)]
    [InlineData(ProviderErrorCategory.TemporaryUnavailability, true)]
    [InlineData(ProviderErrorCategory.AuthenticationFailure, false)]
    [InlineData(ProviderErrorCategory.PermissionDenied, false)]
    public void Retry_classification_never_retries_permission_failures(ProviderErrorCategory category, bool retryable) =>
        Assert.Equal(retryable, FootballPagination.Retryable(new(category, "test")));
}
