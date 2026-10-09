using BetStats.Infrastructure;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using System.Text.Json;
using BetStats.Infrastructure.Quality;

var demo = args.Contains("--synthetic-demo", StringComparer.Ordinal);
var approve = args.Contains("--approve-synthetic", StringComparer.Ordinal);
var builder = Host.CreateApplicationBuilder(args.Where(a => a is not ("--synthetic-demo" or "--approve-synthetic")).ToArray());
builder.Services.AddPersistence(builder.Configuration);
using var host = builder.Build();
if (builder.Configuration["Pipeline:Action"] is not null)
{
    if (demo || new[] { "FootballHistory:Action", "Model:Action", "Backtest:Action", "Results:Action", "Coverage:Action", "Quality:Action", "Dataset:Action" }.Any(key => builder.Configuration[key] is not null))
        throw new ArgumentException("Choose one explicit Worker action.");
    using var scope = host.Services.CreateScope();
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    try { Environment.ExitCode = await BetStats.Infrastructure.Pipeline.PipelineOperatorCommand.RunAsync(scope.ServiceProvider, builder.Configuration, builder.Environment.IsDevelopment(), cancellation.Token); }
    catch (Exception error) when (BetStats.Infrastructure.Pipeline.PipelineOperatorCommand.InfrastructureFailure(error))
    {
        Console.WriteLine(JsonSerializer.Serialize(new { Infrastructure = "Unavailable", ProviderDataReadiness = "Unknown", FailureCategory = "database_unavailable" }));
        Environment.ExitCode = 1;
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { FailureCategory = "shutdown_requested" })); Environment.ExitCode = 1;
    }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or JsonException or UnauthorizedAccessException or IngestionDeniedException)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { FailureCategory = "operator_request_rejected" })); Environment.ExitCode = 1;
    }
}
else if (builder.Configuration["FootballHistory:Action"] is not null)
{
    if (demo || new[] { "Model:Action", "Backtest:Action", "Results:Action", "Coverage:Action", "Quality:Action", "Dataset:Action" }.Any(key => builder.Configuration[key] is not null))
        throw new ArgumentException("Choose one explicit Worker action.");
    using var scope = host.Services.CreateScope();
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Environment.ExitCode = await FootballHistoryOperatorCommand.RunAsync(scope.ServiceProvider, builder.Configuration, builder.Environment.IsDevelopment(), cancellation.Token);
}
else if (builder.Configuration["Model:Action"] is not null)
{
    if (demo || new[] { "Backtest:Action", "Results:Action", "Coverage:Action", "Quality:Action", "Dataset:Action" }.Any(key => builder.Configuration[key] is not null))
        throw new ArgumentException("Choose one explicit Worker action.");
    using var scope = host.Services.CreateScope();
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Environment.ExitCode = await BetStats.Infrastructure.Evaluation.ModelOperatorCommand.RunAsync(scope.ServiceProvider, builder.Configuration, builder.Environment.IsDevelopment(), cancellation.Token);
}
else if (builder.Configuration["Backtest:Action"] is not null)
{
    if (demo || new[] { "Results:Action", "Coverage:Action", "Quality:Action", "Dataset:Action" }.Any(key => builder.Configuration[key] is not null))
        throw new ArgumentException("Choose one explicit Worker action.");
    using var scope = host.Services.CreateScope();
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Environment.ExitCode = await BetStats.Infrastructure.Evaluation.BacktestOperatorCommand.RunAsync(scope.ServiceProvider, builder.Configuration, builder.Environment.IsDevelopment(), cancellation.Token);
}
else if (builder.Configuration["Results:Action"] is { } resultAction)
{
    if (!builder.Environment.IsDevelopment() || demo || builder.Configuration["Coverage:Action"] is not null ||
        builder.Configuration["Quality:Action"] is not null || builder.Configuration["Dataset:Action"] is not null)
        throw new ArgumentException("Choose the explicit Development-only results demo action.");
    using var scope = host.Services.CreateScope();
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    if (resultAction == "demo")
    {
        var reports = await SyntheticFootballResultsDemo.RunAsync(scope.ServiceProvider, approve, cancellation.Token);
        Console.WriteLine(JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = reports.Any(r => r.Outcome is not (ImportOutcome.Succeeded or ImportOutcome.Reused)) ? 1 : 0;
    }
    else Environment.ExitCode = await BetStats.Infrastructure.Football.ResultOperatorCommand.RunAsync(scope.ServiceProvider, builder.Configuration, true, cancellation.Token);
}
else if (builder.Configuration["Coverage:Action"] is not null)
{
    if (demo || builder.Configuration["Quality:Action"] is not null || builder.Configuration["Dataset:Action"] is not null)
        throw new ArgumentException("Choose one explicit Worker action.");
    using var scope = host.Services.CreateScope();
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Environment.ExitCode = await BetStats.Infrastructure.Coverage.CoverageOperatorCommand.RunAsync(scope.ServiceProvider, builder.Configuration, builder.Environment.IsDevelopment(), cancellation.Token);
}
else if (builder.Configuration["Dataset:Action"] is not null)
{
    if (demo || builder.Configuration["Quality:Action"] is not null) throw new ArgumentException("Choose one explicit Worker action.");
    using var scope = host.Services.CreateScope();
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Environment.ExitCode = await BetStats.Infrastructure.Datasets.DatasetOperatorCommand.RunAsync(scope.ServiceProvider, builder.Configuration, cancellation.Token);
}
else if (builder.Configuration["Quality:Action"] is not null)
{
    if (demo) throw new ArgumentException("Choose one explicit Worker action.");
    using var scope = host.Services.CreateScope();
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Environment.ExitCode = await QualityOperatorCommand.RunAsync(scope.ServiceProvider, builder.Configuration, cancellation.Token);
}
else if (demo)
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
