[CmdletBinding()]
param([Parameter(Mandatory)][string]$Container, [Parameter(Mandatory)][string]$User,
    [Parameter(Mandatory)][string]$Destination, [Parameter(Mandatory)][switch]$OfflineConfirmed)
$ErrorActionPreference = 'Stop'
if (!$OfflineConfirmed) { throw 'Stop API, Web, Worker and all RAW maintenance first.' }
if (!$env:ConnectionStrings__BetStats -or !$env:Ingestion__RawStoragePath) { throw 'Set administrative ConnectionStrings__BetStats and Ingestion__RawStoragePath in this process.' }
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project "$PSScriptRoot/../src/BetStats.Worker" -c Release --no-build -- --Operations:Action=backup "--Operations:Container=$Container" "--Operations:User=$User" "--Operations:Directory=$Destination" --Operations:OfflineConfirmed=true
if ($LASTEXITCODE -ne 0) { throw 'Backup failed. Directory is not a certified backup.' }
