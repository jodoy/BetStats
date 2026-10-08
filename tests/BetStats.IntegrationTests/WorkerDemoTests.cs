using System.Diagnostics;
using System.Text.Json;
using BetStats.Application.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace BetStats.IntegrationTests;

public sealed class WorkerDemoTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Explicit_worker_command_runs_offline_demo_and_repeat_without_duplicate_observations()
    {
        var root = Path.Combine(Path.GetTempPath(), "betstats-worker-demo-" + Guid.NewGuid().ToString("N"));
        try
        {
            var reports = await Run();
            Assert.Equal(new[] { ImportOutcome.Partial, ImportOutcome.Partial, ImportOutcome.Succeeded, ImportOutcome.Reused }, reports.Select(r => r.Outcome));
            await using var context = fixture.CreateContext();
            var count = await context.Observations.CountAsync();
            var repeated = await Run(); Assert.Equal(4, repeated.Length); Assert.Equal(count, await context.Observations.CountAsync());
            Assert.Equal(8, await context.RawPayloads.CountAsync()); Assert.Equal(16, await context.IngestionAuditEvents.CountAsync());
            Assert.All(reports, r => Assert.Equal(1, r.RetrievedPayloads));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        async Task<ImportReport[]> Run()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "BetStats.slnx"))) repository = repository.Parent;
            Assert.NotNull(repository);
            var worker = Path.Combine(repository!.FullName, "src", "BetStats.Worker", "bin", "Release", "net10.0", "BetStats.Worker.dll");
            Assert.True(File.Exists(worker), "Build BetStats.slnx in Release before running the demo test.");
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = repository.FullName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { worker, "--synthetic-demo", "--approve-synthetic", "--Logging:LogLevel:Default=Warning" }) start.ArgumentList.Add(argument);
            start.Environment["ConnectionStrings__BetStats"] = fixture.GetConnectionString();
            start.Environment["Ingestion__RawStoragePath"] = root;
            start.Environment["Ingestion__LiveEnabled"] = "false";
            using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            _ = await error; Assert.Equal(0, process.ExitCode);
            return JsonSerializer.Deserialize<ImportReport[]>(await output)!;
        }
    }
}
