using BetStats.Infrastructure;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using System.Text.Json;

var demo = args.Contains("--synthetic-demo", StringComparer.Ordinal);
var approve = args.Contains("--approve-synthetic", StringComparer.Ordinal);
var builder = Host.CreateApplicationBuilder(args.Where(a => a is not ("--synthetic-demo" or "--approve-synthetic")).ToArray());
builder.Services.AddPersistence(builder.Configuration);
using var host = builder.Build();
if (demo)
{
    using var scope = host.Services.CreateScope();
    var services = scope.ServiceProvider;
    var source = await SyntheticFootballDemo.PrepareAsync(services.GetRequiredService<BetStatsDbContext>(), approve);
    var ingestion = services.GetRequiredService<FootballIngestion>();
    var budget = services.GetRequiredService<RequestBudget>();
    var clock = services.GetRequiredService<TimeProvider>();
    var live = bool.TryParse(builder.Configuration["Ingestion:LiveEnabled"], out var configuredLive) && configuredLive;
    var reports = new List<ImportReport>();
    foreach (var csv in new[] { SyntheticFootballDemo.Csv, SyntheticFootballDemo.Csv, SyntheticFootballDemo.CorrectionCsv, SyntheticFootballDemo.CorrectionCsv })
        reports.Add(await ingestion.RunAsync(new FootballDataFixtureAdapter(source, SyntheticFootballDemo.Bytes(csv), clock, live), SyntheticFootballDemo.Scope, budget));
    Console.WriteLine(JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
    Environment.ExitCode = reports.Any(r => r.Outcome is ImportOutcome.Failed or ImportOutcome.Denied or ImportOutcome.Interrupted) ? 1 : 0;
}
else await host.RunAsync();
