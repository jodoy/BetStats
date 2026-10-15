[CmdletBinding()]
param([Parameter(Mandatory)][switch]$OfflineConfirmed)
$ErrorActionPreference = 'Stop'
if (!$OfflineConfirmed -or !$env:ConnectionStrings__BetStats -or !$env:BETSTATS_ADMIN_PASSWORD -or !$env:BETSTATS_APP_PASSWORD) { throw 'Stop all application processes; set bootstrap connection and distinct admin/app passwords in environment.' }
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project "$PSScriptRoot/../src/BetStats.Worker" -c Release --no-build -- --Operations:Action=configure-roles
if ($LASTEXITCODE -ne 0) { throw 'Role provisioning failed; inspect using a protected administrative session.' }
