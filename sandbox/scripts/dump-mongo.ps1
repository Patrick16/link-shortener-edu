<#
.SYNOPSIS
    Dumps clicks_meta_db from the running `mongo1` container (a member of the `rs0` replica set)
    into a timestamped .archive.gz file under sandbox/backups/ (gitignored) - mongodump's own
    binary archive format, read back by restore-mongo.ps1 via mongorestore. Same on-demand-snapshot
    role as dump-db.ps1, just for Mongo instead of Postgres.

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
    $containerId = docker compose ps -q mongo1
    if (-not $containerId) {
        Write-Error 'mongo1 container not found/running - start the stack first.'
    }

    # Full replica-set URI, not --db=clicks_meta_db against mongo1's own localhost connection -
    # mongo1 isn't guaranteed to BE the primary (Mongo can fail over with zero involvement from this
    # project, same as Redis Sentinel - see pitfalls/*.md), and the URI lets the driver itself find
    # whichever member actually holds that role right now, same connection shape the app itself uses
    # (see ConnectionStrings__Mongo in docker-compose.yml).
    Write-Host "==> Dumping clicks_meta_db from the mongo1 container" -ForegroundColor Cyan
    docker compose exec -T mongo1 mongodump --uri="mongodb://mongo1:27017,mongo2:27017,mongo3:27017/clicks_meta_db?replicaSet=rs0" --archive="$containerTmpFile" --gzip
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'mongodump failed - see output above.'
    }

    docker cp "${containerId}:$containerTmpFile" $outFile
    docker compose exec -T mongo1 rm -f $containerTmpFile
} finally {
    Pop-Location
}

Write-Host "Dump written to: $outFile" -ForegroundColor Green
Write-Host "Restore it with: .\sandbox\scripts\restore-mongo.ps1 -DumpFile '$outFile'"
