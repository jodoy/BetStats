using BetStats.Application.Identity;
using BetStats.Application.Observations;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BetStats.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IObservationHistory, ObservationHistory>();
        services.AddScoped<IIdentityResolutionHistory, IdentityResolutionHistory>();
        services.AddDbContext<BetStatsDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("BetStats");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("Configure ConnectionStrings:BetStats before using persistence.");
            }
            options.UseNpgsql(connectionString);
        });
        return services;
    }
}
