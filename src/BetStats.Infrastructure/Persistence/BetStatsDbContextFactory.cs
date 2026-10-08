using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace BetStats.Infrastructure.Persistence;

public sealed class BetStatsDbContextFactory : IDesignTimeDbContextFactory<BetStatsDbContext>
{
    public BetStatsDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var options = new DbContextOptionsBuilder<BetStatsDbContext>();
        var connectionString = configuration.GetConnectionString("BetStats");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // The provider model can be generated offline. Database updates still
            // require explicit configuration; no fallback credentials are used.
            options.UseNpgsql();
        }
        else
        {
            options.UseNpgsql(connectionString);
        }
        return new BetStatsDbContext(options.Options);
    }
}
