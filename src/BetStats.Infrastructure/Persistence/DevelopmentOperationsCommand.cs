using BetStats.Application.Ingestion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BetStats.Infrastructure.Persistence;

public static class DevelopmentOperationsCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, IConfiguration configuration, bool development, CancellationToken token)
    {
        if (!development) throw new UnauthorizedAccessException("Development operations only.");
        var path = configuration["Operations:Directory"] ?? "";
        if (configuration["Operations:Action"] == "verify-backup")
        {
            _ = await DevelopmentBackup.VerifySealedAsync(path, token);
            Console.WriteLine("{\"Backup\":\"Verified\"}"); return 0;
        }
        var db = services.GetRequiredService<BetStatsDbContext>();
        switch (configuration["Operations:Action"])
        {
            case "backup":
                await DevelopmentBackup.CreateAsync(db, services.GetRequiredService<IRawPayloadStore>(), configuration["Operations:Container"] ?? "",
                    configuration["Operations:User"] ?? "", path, configuration["Operations:OfflineConfirmed"] == "true", token); break;
            case "verify-restore": await DevelopmentBackup.VerifyRestoredAsync(db, path, token); break;
            case "configure-roles":
                await using (var connection = new NpgsqlConnection(configuration.GetConnectionString("BetStats")))
                {
                    await connection.OpenAsync(token);
                    await RuntimeDatabaseRole.ConfigureAsync(connection, Environment.GetEnvironmentVariable("BETSTATS_ADMIN_PASSWORD") ?? "",
                        Environment.GetEnvironmentVariable("BETSTATS_APP_PASSWORD") ?? "", token);
                }
                break;
            default: throw new ArgumentException("Unknown Development operation.");
        }
        Console.WriteLine("{\"Operation\":\"Completed\",\"Scope\":\"DevelopmentOnly\"}");
        return 0;
    }
}
