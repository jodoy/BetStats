using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace BetStats.IntegrationTests;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("betstats_tests")
        .WithUsername("betstats_tests")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();

    // No external configuration is accepted. This is always an isolated,
    // disposable container, with a random host port and no shared volume.
    public BetStatsDbContext CreateContext() => new(new DbContextOptionsBuilder<BetStatsDbContext>()
        .UseNpgsql(container.GetConnectionString()).Options);

    public async Task InitializeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await container.StartAsync(timeout.Token);
            await using var context = CreateContext();
            await context.Database.MigrateAsync(timeout.Token);
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync() => await container.DisposeAsync();
}
