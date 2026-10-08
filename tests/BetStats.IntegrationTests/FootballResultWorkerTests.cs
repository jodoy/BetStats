using System.Diagnostics;
using System.Text.Json;
using BetStats.Infrastructure.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace BetStats.IntegrationTests;

public sealed class FootballResultWorkerTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Explicit_development_worker_demo_runs_the_authorized_pipeline_and_reuses_rows()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "BetStats.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var worker = Path.Combine(repository.FullName, "src", "BetStats.Worker", "bin", "Release", "net10.0", "BetStats.Worker.dll");
        Assert.True(File.Exists(worker), "Build the complete solution in Release before running Worker tests.");
        var root = Path.Combine(Path.GetTempPath(), "betstats-result-worker-" + Guid.NewGuid().ToString("N"));
        try
        {
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = repository.FullName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { worker, "--Logging:LogLevel:Default=Warning", "--Results:Action=demo", "--approve-synthetic" }) start.ArgumentList.Add(arg);
            start.Environment["DOTNET_ENVIRONMENT"] = "Development";
            start.Environment["ConnectionStrings__BetStats"] = fixture.GetConnectionString();
            start.Environment["Ingestion__RawStoragePath"] = root;
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
            Assert.Equal(0, child.ExitCode); Assert.Empty(await errors);
            using var reports = JsonDocument.Parse(await output);
            Assert.Equal(new[] { 1, 6, 1, 6 }, reports.RootElement.EnumerateArray().Select(x => x.GetProperty("Outcome").GetInt32()).ToArray());
            await using var db = fixture.CreateContext();
            var source = await db.DataSources.SingleAsync(s => s.Code == SyntheticFootballResultsDemo.SourceCode);
            Assert.Equal(7, await db.FootballResults.CountAsync(r => r.SourceId == source.Id));
            Assert.Equal(4, await db.RawPayloads.CountAsync(r => r.DataSourceId == source.Id));
            Assert.Equal(8, await db.IngestionAuditEvents.CountAsync(r => r.DataSourceId == source.Id));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
