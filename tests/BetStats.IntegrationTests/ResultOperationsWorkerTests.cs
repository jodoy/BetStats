using System.Diagnostics;
using System.Text.Json;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BetStats.IntegrationTests;

public sealed class ResultOperationsWorkerTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Worker_routes_the_explicit_clock_demo_without_startup_import_or_fixed_year()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "BetStats.slnx"))) repo = repo.Parent;
        Assert.NotNull(repo);
        var rawRoot = Path.Combine(Path.GetTempPath(), "betstats-bs010-worker-" + Guid.NewGuid().ToString("N"));
        var sourceCode = "bs010-worker-" + Guid.NewGuid().ToString("N");
        try
        {
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = repo.FullName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { Path.Combine(repo.FullName, "src", "BetStats.Worker", "bin", "Release", "net10.0", "BetStats.Worker.dll"),
                "--Logging:LogLevel:Default=Warning", "--Results:Action=demo-clock", "--Results:OperatorId=operator:test", "--Results:Reason=Explicit worker fixture", "--Results:Approve=true", "--Results:SourceCode=" + sourceCode }) start.ArgumentList.Add(arg);
            start.Environment["DOTNET_ENVIRONMENT"] = "Development";
            start.Environment["ConnectionStrings__BetStats"] = fixture.GetConnectionString(); start.Environment["Ingestion__RawStoragePath"] = rawRoot;
            using var child = Process.Start(start)!; var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await child.WaitForExitAsync(timeout.Token); } catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
            Assert.Equal(0, child.ExitCode); Assert.Empty(await stderr);
            using var json = JsonDocument.Parse(await stdout);
            Assert.Equal("demo-clock", json.RootElement.GetProperty("Action").GetString());
            Assert.Equal(new[] { "Succeeded", "Reused", "Succeeded", "Reused" }, json.RootElement.GetProperty("Result").GetProperty("Imports").EnumerateArray().Select(r => r.GetProperty("Outcome").GetString()).ToArray());
            await using var db = fixture.CreateContext(); var source = await db.DataSources.SingleAsync(s => s.Code == sourceCode);
            Assert.Equal(5, await db.FootballResults.CountAsync(r => r.SourceId == source.Id));
            Assert.All(await db.FootballRawContexts.Where(c => c.SourceId == source.Id).ToArrayAsync(), c => Assert.Equal("clock-controlled-fiction", c.SeasonReference));
            var policy = await db.SourcePolicies.Include(p => p.Permissions).SingleAsync(p => p.DataSourceId == source.Id);
            Assert.DoesNotContain(policy.Permissions, p => p.Purpose == BetStats.Domain.Governance.DataPurpose.PublicDisplay && p.Decision == BetStats.Domain.Governance.PermissionDecision.Allowed);
        }
        finally { if (Directory.Exists(rawRoot)) Directory.Delete(rawRoot, true); }
    }
}
