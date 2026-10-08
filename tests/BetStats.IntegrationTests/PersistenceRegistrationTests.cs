using BetStats.Infrastructure;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BetStats.IntegrationTests;

public sealed class PersistenceRegistrationTests
{
    [Fact]
    public void Registration_and_context_resolution_do_not_connect_to_database()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Reserved localhost port, deliberately unavailable; no credentials.
            ["ConnectionStrings:BetStats"] = "Host=127.0.0.1;Port=1;Database=registration_probe;Timeout=1"
        }).Build();
        var services = new ServiceCollection().AddPersistence(configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BetStatsDbContext>();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", context.Database.ProviderName);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void Missing_configuration_is_reported_only_when_persistence_is_requested()
    {
        var services = new ServiceCollection().AddPersistence(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        var error = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<BetStatsDbContext>());
        Assert.Contains("ConnectionStrings:BetStats", error.Message);
    }
}
