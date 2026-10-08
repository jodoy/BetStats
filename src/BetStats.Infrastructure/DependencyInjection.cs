using BetStats.Application.Identity;
using BetStats.Application.Governance;
using BetStats.Application.Providers;
using BetStats.Application.Observations;
using BetStats.Application.Ingestion;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Application.Quality;
using BetStats.Infrastructure.Quality;
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
        services.AddScoped<ISourceOperationalStatus, SourceOperationalStatusReader>();
        services.AddScoped<AuthorizedProviderExecutor>();
        services.AddScoped<IFootballIngestionPersistence, FootballIngestionPersistence>();
        services.AddScoped<FootballIngestion>();
        services.AddScoped<BetStats.Application.Football.IFootballResults, BetStats.Infrastructure.Football.PostgreSqlFootballResults>();
        services.AddScoped<BetStats.Application.Football.IFootballResultDatasets, BetStats.Infrastructure.Football.PostgreSqlFootballResultDatasets>();
        services.AddScoped<BetStats.Application.Football.IResultGovernance, BetStats.Infrastructure.Football.PostgreSqlResultGovernance>();
        services.AddScoped<BetStats.Application.Football.IResultDatasetOperations, BetStats.Infrastructure.Football.ResultDatasetOperations>();
        services.AddScoped<BetStats.Application.Football.IDevelopmentFootball, BetStats.Infrastructure.Football.DevelopmentFootball>();
        services.AddScoped<IIdentityReview, IdentityReview>();
        services.AddScoped<IDataReconciliation, DataReconciliation>();
        services.AddScoped<IAnalyticalQualityGate, AnalyticalQualityGate>();
        services.AddScoped<IQualityReports, QualityReports>();
        services.AddScoped<BetStats.Application.Coverage.IHistoricalCoverage, BetStats.Infrastructure.Coverage.HistoricalCoverage>();
        services.AddScoped<BetStats.Application.Datasets.IDatasets, BetStats.Infrastructure.Datasets.PostgreSqlDatasets>();
        services.AddSingleton<IFootballMetadataParser, FootballFixtureParser>();
        services.AddSingleton<IRawPayloadStore>(_ => new FileSystemRawPayloadStore(configuration["Ingestion:RawStoragePath"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetStats", "raw")));
        services.AddSingleton(provider => new RequestBudget(new(10, 100, 1, TimeSpan.FromSeconds(30)), provider.GetRequiredService<TimeProvider>()));
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
