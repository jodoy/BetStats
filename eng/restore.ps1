[CmdletBinding()]
param([Parameter(Mandatory)][string]$Backup, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath (Join-Path $Backup 'COMPLETE')) -or (Test-Path -LiteralPath $Destination) -or ![IO.Path]::IsPathFullyQualified($Destination)) { throw 'Sealed backup and new absolute destination required.' }
$env:DOTNET_ENVIRONMENT = 'Development'
$env:BETSTATS_API_FOOTBALL_ENABLED = 'false'
dotnet run --project "$PSScriptRoot/../src/BetStats.Worker" -c Release --no-build -- --Operations:Action=verify-backup "--Operations:Directory=$Backup"
if ($LASTEXITCODE -ne 0) { throw 'Backup verification failed before restore.' }
$name = 'betstats-restore-' + [Guid]::NewGuid().ToString('N')
$savedPassword = $env:POSTGRES_PASSWORD
$env:POSTGRES_PASSWORD = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
try {
    docker run --detach --name $name -e POSTGRES_PASSWORD -e POSTGRES_DB=betstats_restore -p 127.0.0.1::5432 postgres:17-alpine | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Disposable PostgreSQL creation failed.' }
    Write-Host "Restore container: $name"
    $ready = $false
    # The image starts a temporary socket-only server during initialization.
    # Require TCP readiness so pg_restore cannot race that server's shutdown.
    for ($i=0; $i -lt 60; $i++) { docker exec $name pg_isready -h 127.0.0.1 -U postgres -d betstats_restore *> $null; if ($LASTEXITCODE -eq 0) { $ready=$true; break }; Start-Sleep -Seconds 1 }
    if (!$ready) { throw 'Restore PostgreSQL readiness timeout.' }
    New-Item -ItemType Directory -Path $Destination | Out-Null
    if ($IsWindows) {
        $owner=[Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl=[Security.AccessControl.DirectorySecurity]::new(); $acl.SetAccessRuleProtection($true,$false); $acl.SetOwner($owner)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($owner,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
        Set-Acl -LiteralPath $Destination -AclObject $acl
    } else { & chmod 700 $Destination; if ($LASTEXITCODE -ne 0) { throw 'Restore permissions failed.' } }
    foreach ($item in @('database.dump','manifest.json','COMPLETE','raw')) { Copy-Item -LiteralPath (Join-Path $Backup $item) -Destination $Destination -Recurse }
    docker cp (Join-Path $Destination 'database.dump') "${name}:/tmp/database.dump"
    if ($LASTEXITCODE -ne 0) { throw 'Dump transfer failed.' }
    docker exec $name pg_restore -U postgres -d betstats_restore --exit-on-error --no-owner --no-acl /tmp/database.dump
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed; do not use this container.' }
    $port = (docker inspect --format '{{(index (index .NetworkSettings.Ports "5432/tcp") 0).HostPort}}' $name).Trim()
    if ($LASTEXITCODE -ne 0 -or $port -notmatch '^\d+$') { throw 'Restore port unavailable.' }
    $env:ConnectionStrings__BetStats = "Host=127.0.0.1;Port=$port;Database=betstats_restore;Username=postgres;Password=$env:POSTGRES_PASSWORD;Include Error Detail=false"
    $env:Ingestion__RawStoragePath = Join-Path $Destination 'raw'
    $env:DOTNET_ENVIRONMENT = 'Development'
    dotnet run --project "$PSScriptRoot/../src/BetStats.Worker" -c Release --no-build -- --Operations:Action=verify-restore "--Operations:Directory=$Destination"
    if ($LASTEXITCODE -ne 0) { throw 'DB/RAW binding verification failed.' }
    Write-Host "Verified isolated restore: $name; port $port; RAW $env:Ingestion__RawStoragePath. This process now targets only the disposable restore. No provider execution was enabled."
} finally { $env:POSTGRES_PASSWORD = $savedPassword }
