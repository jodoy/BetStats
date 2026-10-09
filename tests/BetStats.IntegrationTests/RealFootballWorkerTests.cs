using System.Diagnostics;
using System.Text.Json;
using BetStats.Infrastructure.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace BetStats.IntegrationTests;

public sealed class RealFootballWorkerTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Actual_worker_plans_captures_inspects_and_denies_non_development()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "BetStats.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var worker = Path.Combine(repository.FullName, "src", "BetStats.Worker", "bin", "Release", "net10.0", "BetStats.Worker.dll");
        var root = Path.Combine(Path.GetTempPath(), "bs013-worker-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var db = fixture.CreateContext(); var source = await SyntheticFootballDemo.PrepareAsync(db, true, "bs013-worker-" + Guid.NewGuid().ToString("N"));
            var file = Path.Combine(root, "fiction.csv"); await File.WriteAllTextAsync(file, "Div,Date,HomeTeam,AwayTeam,FTHG,FTAG,MatchId\nFICT,02/01/2026,Amber Comets,Cobalt Owls,1,0,operator-event\n");
            async Task<(int Exit, string Output)> Run(string action, string environment, params string[] extra)
            {
                var start = new ProcessStartInfo("dotnet") { WorkingDirectory = repository.FullName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { worker, "--Logging:LogLevel:Default=Warning", "--FootballHistory:Action=" + action,
                    "--FootballHistory:OperatorId=test:operator", "--FootballHistory:Reason=Explicit fictional worker test" }.Concat(extra)) start.ArgumentList.Add(arg);
                start.Environment["DOTNET_ENVIRONMENT"] = environment; start.Environment["ConnectionStrings__BetStats"] = fixture.GetConnectionString(); start.Environment["Ingestion__RawStoragePath"] = root;
                using var child = Process.Start(start)!; var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                try { await child.WaitForExitAsync(timeout.Token); } catch { if (!child.HasExited) child.Kill(true); throw; }
                await errors; return (child.ExitCode, await output);
            }
            var planned = await Run("plan", "Development", "--FootballHistory:SourceId=" + source, "--FootballHistory:PayloadPath=" + file,
                "--FootballHistory:Competition=FICT", "--FootballHistory:Season=2026-fiction"); Assert.Equal(0, planned.Exit);
            using var document = JsonDocument.Parse(planned.Output); var plan = document.RootElement.GetProperty("Result").GetRawText();
            var operation = Guid.NewGuid();
            var captured = await Run("capture", "Development", "--FootballHistory:OperationId=" + operation, "--FootballHistory:PlanJson=" + plan,
                "--FootballHistory:PayloadPath=" + file, "--FootballHistory:Approve=true"); Assert.Equal(1, captured.Exit); // Waiting for reviewed event.
            Assert.Single(await db.RawPayloads.Where(r => r.DataSourceId == source).ToListAsync());
            var inspected = await Run("inspect", "Development", "--FootballHistory:OperationId=" + operation); Assert.Equal(0, inspected.Exit);
            using var inspectedDocument = JsonDocument.Parse(inspected.Output); Assert.Equal(operation.ToString(), inspectedDocument.RootElement.GetProperty("Result").GetProperty("OperationId").GetString());
            Assert.NotEqual(0, (await Run("readiness", "Production", "--FootballHistory:SourceId=" + source)).Exit);
            Assert.Equal(0, (await Run("readiness", "Development", "--FootballHistory:SourceId=" + source)).Exit);
        }
        finally { Directory.Delete(root, true); }
    }
}
