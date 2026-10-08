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
            var source = (await context.DataSources.SingleAsync()).Id;
            using var listed = JsonDocument.Parse(await Invoke(["--Quality:Action=list", "--Quality:SourceId=" + source]));
            Assert.True(listed.RootElement.GetArrayLength() >= 2);
            var unknown = await context.ProviderIdentities.SingleAsync(i => i.ExternalId == BetStats.Infrastructure.Ingestion.FootballDataCsvParser.TeamReference("FICT", "Unmapped Ravens"));
            var target = await context.Participants.SingleAsync(p => p.Name == "Cobalt Owls");
            using var reviewed = JsonDocument.Parse(await Invoke(["--Quality:Action=review", "--Quality:SourceId=" + source, "--Quality:IdentityId=" + unknown.Id,
                "--Quality:Decision=Approve", "--Quality:TargetKind=Participant", "--Quality:TargetId=" + target.Id, "--Quality:ExpectedVersion=1", "--Quality:OperatorId=operator:worker-test", "--Quality:Reason=Reviewed fictional fixture"]));
            Assert.Equal("accepted", reviewed.RootElement.GetProperty("Result").GetString());
            var raw = await context.RawPayloads.SingleAsync(r => r.IngestionRunId == reports[0].RunId);
            using var reconciliation = JsonDocument.Parse(await Invoke(["--Quality:Action=reconcile", "--Quality:RawId=" + raw.Id, "--Quality:Competition=FICT", "--Quality:Season=2026-fiction",
                "--Quality:OperatorId=operator:worker-test", "--Quality:Reason=Explicit worker reconciliation"]));
            Assert.Equal("Completed", reconciliation.RootElement.GetProperty("Result").GetString());
            var execution = reconciliation.RootElement.GetProperty("ExecutionId").GetGuid();
            using var qualityReport = JsonDocument.Parse(await Invoke(["--Quality:Action=report", "--Quality:ExecutionId=" + execution]));
            Assert.Equal(6, qualityReport.RootElement.GetProperty("Total").GetInt32());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        async Task<ImportReport[]> Run()
            => JsonSerializer.Deserialize<ImportReport[]>(await Invoke(["--synthetic-demo", "--approve-synthetic"]))!;
        async Task<string> Invoke(string[] arguments)
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "BetStats.slnx"))) repository = repository.Parent;
            Assert.NotNull(repository);
            var worker = Path.Combine(repository!.FullName, "src", "BetStats.Worker", "bin", "Release", "net10.0", "BetStats.Worker.dll");
            Assert.True(File.Exists(worker), "Build BetStats.slnx in Release before running the demo test.");
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = repository.FullName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { worker, "--Logging:LogLevel:Default=Warning" }.Concat(arguments)) start.ArgumentList.Add(argument);
            start.Environment["ConnectionStrings__BetStats"] = fixture.GetConnectionString();
            start.Environment["Ingestion__RawStoragePath"] = root;
            start.Environment["Ingestion__LiveEnabled"] = "false";
            using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            _ = await error; Assert.Equal(0, process.ExitCode);
            return await output;
        }
    }
}
