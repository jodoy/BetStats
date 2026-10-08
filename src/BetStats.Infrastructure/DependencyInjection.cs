using BetStats.Application.Identity;
using BetStats.Application.Governance;
using BetStats.Application.Providers;
using BetStats.Application.Observations;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BetStats.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IObservationHistory, ObservationHistory>();
        services.AddScoped<IIdentityResolutionHistory, IdentityResolutionHistory>();
        services.AddScoped<ISourcePolicyHistory, SourcePolicyHistory>();
        services.AddScoped<ISourcePolicyEvaluator, SourcePolicyEvaluator>();
        services.AddScoped<AuthorizedProviderExecutor>();
        services.TryAddSingleton(TimeProvider.System);
        var pageCap = configuration["History:MaximumPageSize"] is { } configuredCap
            ? int.Parse(configuredCap, System.Globalization.CultureInfo.InvariantCulture) : 200;
        services.AddSingleton(new ObservationPageOptions(pageCap));
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
