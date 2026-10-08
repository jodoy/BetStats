using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace BetStats.Infrastructure.Ingestion;

public static class SyntheticFootballResultsDemo
{
    public const string SourceCode = "synthetic-football-results-demo";
    public const string Header = "Div,Date,HomeTeam,AwayTeam,MatchId,Status,ResultBasis,FTHG,FTAG,HTHG,HTAG,PublishedAtUtc\n";
    public const string Csv = Header +
        "FICT,01/10/2026,Amber Comets,Cobalt Owls,result-1,Finished,RegulationTime,2,1,1,0,\n" +
        "FICT,02/10/2026,Silver Foxes,Violet Herons,result-2,Finished,RegulationTime,0,0,0,0,\n" +
        "FICT,03/10/2026,Amber Comets,Silver Foxes,result-3,Finished,RegulationTime,1,0,unknown,unknown,\n" +
        "FICT,04/10/2026,Cobalt Owls,Violet Herons,result-4,Postponed,Unknown,unavailable,unavailable,unavailable,unavailable,\n" +
        "FICT,05/10/2026,Silver Foxes,Cobalt Owls,result-5,Cancelled,Unknown,,,,,\n" +
        "FICT,20/12/2026,Amber Comets,Cobalt Owls,result-target,Scheduled,Unknown,,,,,\n";
    public const string Correction = Header + "FICT,01/10/2026,Amber Comets,Cobalt Owls,result-1,Finished,RegulationTime,2,2,1,0,\n";
    public static IReadOnlySet<string> PublicFixtureHashes { get; } = new HashSet<string>(new[] { Csv, Correction }
        .Select(csv => BetStats.Application.Datasets.CanonicalDatasetJson.Hash(SyntheticFootballDemo.Bytes(csv))), StringComparer.Ordinal);
    public static async Task<IReadOnlyList<ImportReport>> RunAsync(IServiceProvider services, bool approve, CancellationToken token)
    {
        var source = await SyntheticFootballDemo.PrepareAsync(services.GetRequiredService<BetStatsDbContext>(), approve, SourceCode, token, allowSyntheticDisplay: true);
        var ingestion = services.GetRequiredService<FootballIngestion>(); var budget = services.GetRequiredService<RequestBudget>();
        var reports = new List<ImportReport>();
        foreach (var csv in new[] { Csv, Csv, Correction, Correction })
            reports.Add(await ingestion.RunAsync(new FootballDataFixtureAdapter(source, SyntheticFootballDemo.Bytes(csv), services.GetRequiredService<TimeProvider>()), SyntheticFootballDemo.Scope, budget, token));
        return reports;
    }
}
