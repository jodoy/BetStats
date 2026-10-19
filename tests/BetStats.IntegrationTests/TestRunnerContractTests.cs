using System.Diagnostics;

namespace BetStats.IntegrationTests;

public sealed class TestRunnerContractTests
{
    [Theory]
    [InlineData("zero", false)]
    [InlineData("failure", false)]
    [InlineData("full", true)]
    [InlineData("source", false)]
    [InlineData("binary", false)]
    [InlineData("missing", false)]
    [InlineData("buildonly", true)]
    public async Task Runner_rejects_empty_selection_propagates_failures_and_full_selects_three_projects_once(string mode, bool success)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "BetStats.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var root = Path.Combine(Path.GetTempPath(), "betstats-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "eng"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        foreach (var project in new[] { "BetStats.UnitTests", "BetStats.ArchitectureTests", "BetStats.IntegrationTests" })
        {
            var binary = Path.Combine(root, "tests", project, "bin", "Release", "net10.0", project + ".dll");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!); await File.WriteAllTextAsync(binary, "mock compiled artifact");
        }
        try
        {
            File.Copy(Path.Combine(repository.FullName, "eng", "test.ps1"), Path.Combine(root, "eng", "test.ps1"));
            // Isolated process and mock dotnet: this tests the script's selection /
            // exit contract, never recursively runs or skips real application tests.
            var script = $$"""
                $ErrorActionPreference='Stop'
                $global:runnerCalls=@()
                function global:dotnet {
                    if ('{{mode}}' -eq 'failure') { $global:LASTEXITCODE=7; return }
                    if ($args[0] -ne 'test') { $global:LASTEXITCODE=0; return }
                    $global:runnerCalls += $args[1]
                    $directory=$args[[Array]::IndexOf($args,'--results-directory')+1]
                    $logger=$args[[Array]::IndexOf($args,'--logger')+1]
                    $filename=$logger.Split('=')[1]
                    $count=if ('{{mode}}' -eq 'zero') {0} else {1}
                    '<TestRun><ResultSummary><Counters total="'+$count+'" passed="'+$count+'" failed="0" notExecuted="0" /></ResultSummary></TestRun>' | Set-Content (Join-Path $directory $filename)
                    $global:LASTEXITCODE=0
                }
                if ('{{mode}}' -eq 'buildonly') {
                    & "$PSScriptRoot/eng/test.ps1" -BuildOnly
                    if ($global:runnerCalls.Count -ne 0) { exit 10 }
                    & "$PSScriptRoot/eng/test.ps1" -Suite Full -NoBuild
                } else { & "$PSScriptRoot/eng/test.ps1" -Suite Full }
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                if ('{{mode}}' -in @('source','binary','missing')) {
                    if ('{{mode}}' -eq 'source') { 'changed' | Set-Content "$PSScriptRoot/src/Changed.cs" }
                    $binary="$PSScriptRoot/tests/BetStats.UnitTests/bin/Release/net10.0/BetStats.UnitTests.dll"
                    if ('{{mode}}' -eq 'binary') { 'changed' | Set-Content $binary }
                    if ('{{mode}}' -eq 'missing') { Remove-Item -LiteralPath $binary }
                    & "$PSScriptRoot/eng/test.ps1" -Suite Full -NoBuild
                }
                if ('{{mode}}' -in @('full','buildonly') -and ($global:runnerCalls.Count -ne 3 -or @($global:runnerCalls | Select-Object -Unique).Count -ne 3)) { exit 9 }
                "calls=$($global:runnerCalls.Count)"
                """;
            var path = Path.Combine(root, "mock.ps1"); await File.WriteAllTextAsync(path, script);
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-NoProfile", "-File", path }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await process.WaitForExitAsync(timeout.Token);
            var output = await stdout; _ = await stderr;
            Assert.Equal(success, process.ExitCode == 0); if (success) Assert.Contains("calls=3", output);
            if (mode == "failure") Assert.Equal(7, process.ExitCode);
        }
        finally { Directory.Delete(root, true); }
    }
}
