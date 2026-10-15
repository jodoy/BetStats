$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $json = & dotnet list BetStats.slnx package --vulnerable --include-transitive --format json
    if ($LASTEXITCODE -ne 0) { throw 'Dependency audit could not complete.' }
    $report = ($json -join "`n") | ConvertFrom-Json
    if ($report.problems) { throw 'Dependency audit returned problems; inspect the protected build output.' }
    $count=0
    foreach ($project in $report.projects) {
        foreach ($framework in $project.frameworks) {
            foreach ($package in @($framework.topLevelPackages)+@($framework.transitivePackages)) {
                if ($package.vulnerabilities) { $count += @($package.vulnerabilities).Count; Write-Output "$($package.id): $($package.resolvedVersion) has reported vulnerabilities" }
            }
        }
    }
    if ($count -gt 0) { throw "$count reported dependency vulnerabilities." }
    Write-Output 'Dependency vulnerability audit passed.'
} finally { Pop-Location }
