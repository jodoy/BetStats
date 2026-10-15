using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using BetStats.Application.Dashboard;
using BetStats.Application.Ingestion;
using BetStats.Domain.Coverage;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace BetStats.IntegrationTests;

// Synthetic PostgreSQL measurement, not a latency SLO or a benchmark assertion.
public sealed class OperationalPerformanceTests(PostgreSqlFixture fixture, ITestOutputHelper output) : IClassFixture<PostgreSqlFixture>
{
    private sealed class Commands : DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken token = default)
        { Interlocked.Increment(ref Count); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<object> result, CancellationToken token = default)
        { Interlocked.Increment(ref Count); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken token = default)
        { Interlocked.Increment(ref Count); return ValueTask.FromResult(result); }
    }
    private sealed class Window { public int RawReads; }
    private static readonly AsyncLocal<Window?> Measuring = new();
    private sealed record Sample(string Report, int RequestedRecords, int ActualRecords, int SqlCommands, int RawReads, double Milliseconds);
    private static string Csv(int size) => "Div,Date,HomeTeam,AwayTeam,FTR,MatchId\n" + string.Concat(Enumerable.Range(1, size).Select(i =>
        $"FICT,02/01/2026,Amber Comets,Cobalt Owls,H,measurement-{i}\n"));
    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    public async Task Measure_reports_with_explicit_actual_cardinality(int size)
    {
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name == "BetStats.Operations" && instrument.Name == "betstats.operation.events") l.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
            if (Measuring.Value is not { } window) return;
            foreach (var tag in tags) if (tag.Key == "category" && Equals(tag.Value, "raw_read")) Interlocked.Add(ref window.RawReads, checked((int)value));
        }); listener.Start();
        var samples = new List<Sample>();
        async Task Measure(string name, Commands commands, Func<Task<int>> read)
        {
            var before = commands.Count; var window = new Window(); Measuring.Value = window;
            var timer = Stopwatch.StartNew(); int actual;
            try { actual = await read(); } finally { Measuring.Value = null; }
            samples.Add(new(name, size, actual, commands.Count - before, window.RawReads, timer.Elapsed.TotalMilliseconds));
        }
        var qualityCommands = new Commands();
        await using (var s = await new QualityWorkflowTests(fixture).Create(fixture.CreateContext(qualityCommands)))
        {
            var imported = await s.Import(Csv(size));
            await Measure("Quality_Unreviewed", qualityCommands, async () => (await new QualityReports(s.Db, s.Gate).ReadAsync(imported.AttemptId)).Total);
            Assert.Equal(size, samples[^1].ActualRecords);
        }
        var coverageCommands = new Commands();
        await using (var s = await new CoverageWorkflowTests(fixture).Create(fixture.CreateContext(coverageCommands)))
        {
            await s.Ingestion.RunAsync(new FootballDataFixtureAdapter(s.Source, SyntheticFootballDemo.Bytes(Csv(size)), TimeProvider.System),
                new(s.Definition.CompetitionReference, s.Definition.SeasonReference), new(new(100,1000,1,TimeSpan.FromSeconds(30)), TimeProvider.System));
            var claim = await s.Claim(s.Scope(), CoverageStatus.Partial); var query = s.Query(s.Scope(), await s.Now());
            Assert.True(claim.SupportingObservationIds.Length >= size);
            await Measure("Coverage_UnreviewedPartial", coverageCommands, async () => (await s.Coverage.ReportAsync(query)).Items.Sum(i => i.Evidence.SupportingObservationIds.Length));
        }
        var dashboardCommands = new Commands();
        await using (var s = await new ResultOperationsWorkflowTests(fixture).Create(true, configure: services => services.AddDbContext<BetStatsDbContext>(o => o.AddInterceptors(dashboardCommands))))
        {
            _ = await BacktestWorkflowTests.Definition(s);
            var query = await s.Query();
            await Measure("Dashboard_SparseFrozenPage", dashboardCommands, async () => (await s.Get<IDashboardQueries>().FixturesAsync(new() { Competition = query.CompetitionId, Season = query.SeasonId, Limit = size })).Items.Count);
        }
        var json = JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }); output.WriteLine(json);
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "BetStats.slnx"))) root = root.Parent;
        Assert.NotNull(root); var destination = Path.Combine(root.FullName, "artifacts", "performance"); Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, $"bs019c-{size}.json"), json);
        Assert.All(samples, s => { Assert.True(s.SqlCommands > 0); Assert.True(s.Milliseconds >= 0); });
    }
}
