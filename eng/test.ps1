[CmdletBinding()]
param([ValidateSet('Fast','Integration','Migration','Security','Full')][string]$Suite='Fast', [switch]$NoBuild)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (!$NoBuild) {
        dotnet restore BetStats.slnx --locked-mode
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet build BetStats.slnx -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    $projects = switch ($Suite) {
        Fast { @('BetStats.UnitTests','BetStats.ArchitectureTests') }
        Integration { @('BetStats.IntegrationTests') }
        Migration { @('BetStats.IntegrationTests') }
        default { @('BetStats.UnitTests','BetStats.ArchitectureTests','BetStats.IntegrationTests') }
    }
    $filter = switch ($Suite) { Integration {'Category!=Migration'} Migration {'Category=Migration'} Security {'Category=Security'} default {$null} }
    $destination=Join-Path $root ('artifacts/tests/'+$Suite+'-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $total=0; $passed=0; $failed=0; $skipped=0
    foreach ($project in $projects) {
        $arguments=@('test',"tests/$project",'-c','Release','--no-build','--logger',"trx;LogFileName=$project.trx",'--results-directory',$destination)
        if ($filter) { $arguments+=@('--filter',$filter) }
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $trx=Join-Path $destination "$project.trx"
        if (!(Test-Path -LiteralPath $trx)) { throw "Missing results: $project" }
        [xml]$result=Get-Content -LiteralPath $trx -Raw
        $c=$result.TestRun.ResultSummary.Counters
        if (!$c -or [int]$c.total -eq 0) { throw "No tests selected: $project / $Suite" }
        $total += [int]$c.total; $passed += [int]$c.passed; $failed += [int]$c.failed; $skipped += [int]$c.notExecuted
    }
    $summary=[ordered]@{Suite=$Suite;Total=$total;Passed=$passed;Failed=$failed;Skipped=$skipped;ElapsedSeconds=[Math]::Round($timer.Elapsed.TotalSeconds,2)}
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'summary.json') -Encoding utf8
    $summary | ConvertTo-Json -Compress | Write-Output
    if ($failed -gt 0 -or $skipped -gt 0) { exit 1 }
} finally { Pop-Location }
