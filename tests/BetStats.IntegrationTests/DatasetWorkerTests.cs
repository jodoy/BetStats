using System.Diagnostics;
using System.Text.Json;

namespace BetStats.IntegrationTests;

public sealed class DatasetWorkerTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Explicit_worker_build_inspect_verify_compare_commands_execute_offline()
    {
        var root = Path.Combine(Path.GetTempPath(), "betstats-dataset-worker-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var built = JsonDocument.Parse(await Invoke(["--Dataset:Action=build-synthetic", "--Dataset:ApproveSynthetic=true", "--Dataset:SourceCode=synthetic-dataset-worker",
                "--Dataset:TargetDate=2026-12-01", "--Dataset:OperatorId=operator:worker", "--Dataset:Reason=Fictional-demo"]));
            var id = built.RootElement.GetProperty("SnapshotId").GetGuid();
            using var inspected = JsonDocument.Parse(await Invoke(["--Dataset:Action=inspect", "--Dataset:SnapshotId=" + id]));
            Assert.Equal(id, inspected.RootElement.GetProperty("Id").GetGuid());
            using var verified = JsonDocument.Parse(await Invoke(["--Dataset:Action=verify", "--Dataset:SnapshotId=" + id]));
            Assert.True(verified.RootElement.GetProperty("ArtifactIntegrity").GetBoolean()); Assert.True(verified.RootElement.GetProperty("FeaturesReproducible").GetBoolean());
            using var deep = JsonDocument.Parse(await Invoke(["--Dataset:Action=verify-deep", "--Dataset:SnapshotId=" + id]));
            Assert.True(deep.RootElement.GetProperty("RawAvailable").GetBoolean()); Assert.True(deep.RootElement.GetProperty("RawHashVerified").GetBoolean());
            using var compared = JsonDocument.Parse(await Invoke(["--Dataset:Action=compare", "--Dataset:LeftId=" + id, "--Dataset:RightId=" + id, "--Dataset:Offset=0", "--Dataset:Limit=20"]));
            Assert.Equal(0, compared.RootElement.GetProperty("Total").GetInt32());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        async Task<string> Invoke(string[] arguments)
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "BetStats.slnx"))) repository = repository.Parent;
            Assert.NotNull(repository);
            var worker = Path.Combine(repository.FullName, "src", "BetStats.Worker", "bin", "Release", "net10.0", "BetStats.Worker.dll");
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = repository.FullName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { worker, "--Logging:LogLevel:Default=Warning" }.Concat(arguments)) start.ArgumentList.Add(argument);
            start.Environment["ConnectionStrings__BetStats"] = fixture.GetConnectionString(); start.Environment["Ingestion__RawStoragePath"] = root;
            using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); throw; }
            var stdout = await output; var stderr = await error; Assert.True(process.ExitCode == 0, stdout + stderr); return stdout.Trim();
        }
    }
}
