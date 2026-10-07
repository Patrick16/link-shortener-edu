<#
.SYNOPSIS
    Dumps clicks_meta_db from the `rs0` Mongo replica set (whichever of mongo1/mongo2/mongo3 is
    actually running) into a timestamped .archive.gz file under sandbox/backups/ (gitignored) -
    mongodump's own binary archive format, read back by restore-mongo.ps1 via mongorestore. Same
    on-demand-snapshot role as dump-db.ps1, just for Mongo instead of Postgres.

.EXAMPLE
    .\sandbox\scripts\dump-mongo.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$sandboxRoot = Split-Path -Parent $PSScriptRoot
$backupsDir = Join-Path $sandboxRoot 'backups'

if (-not (Test-Path $backupsDir)) {
    New-Item -ItemType Directory -Path $backupsDir | Out-Null
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outFile = Join-Path $backupsDir "mongo-$timestamp.archive.gz"
# mongodump's own binary archive - written inside the container first and pulled out with
# `docker cp`, rather than redirecting the container's stdout from PowerShell, which mangles
# binary data through its text-pipeline encoding (fine for dump-db.ps1's plain-SQL output, not for
# this).
$containerTmpFile = "/tmp/mongo-$timestamp.archive.gz"

Push-Location $sandboxRoot
try {
    # Picks whichever of the three replica-set members is actually up to exec the mongodump binary
    # in, rather than assuming mongo1 specifically - mongo1 being down shouldn't block a backup when
    # mongo2/mongo3 are healthy. The connection URI below (listing all three, replicaSet=rs0) is a
    # separate concern - that's what lets the driver find the current PRIMARY for the actual read;
    # this is just about which container has a shell to run the command from.
    $execService = 'mongo1', 'mongo2', 'mongo3' | Where-Object { docker compose ps -q $_ } | Select-Object -First 1
    if (-not $execService) {
        Write-Error 'No mongo1/mongo2/mongo3 container found/running - start the stack first.'
    }
    $containerId = docker compose ps -q $execService

    Write-Host "==> Dumping clicks_meta_db (via $execService) from the mongo replica set" -ForegroundColor Cyan
    docker compose exec -T $execService mongodump --uri="mongodb://mongo1:27017,mongo2:27017,mongo3:27017/clicks_meta_db?replicaSet=rs0" --archive="$containerTmpFile" --gzip
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'mongodump failed - see output above.'
    }

    docker cp "${containerId}:$containerTmpFile" $outFile
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'docker cp failed to pull the dump out of the container - see output above.'
    }
    docker compose exec -T $execService rm -f $containerTmpFile
} finally {
    Pop-Location
}

Write-Host "Dump written to: $outFile" -ForegroundColor Green
Write-Host "Restore it with: .\sandbox\scripts\restore-mongo.ps1 -DumpFile '$outFile'"
