<#
.SYNOPSIS
    Restores a mongodump archive (created by dump-mongo.ps1) into the `rs0` Mongo replica set
    (whichever of mongo1/mongo2/mongo3 is actually running). --drop replaces clicks_meta_db's
    existing collections with the dump's contents, same full-replace spirit as restore-db.ps1's
    psql replay.

.PARAMETER DumpFile
    Path to the .archive.gz file to restore. Defaults to the newest mongo-*.archive.gz in
    sandbox/backups/.

.EXAMPLE
    .\sandbox\scripts\restore-mongo.ps1
.EXAMPLE
    .\sandbox\scripts\restore-mongo.ps1 -DumpFile '.\sandbox\backups\mongo-20261007-000000.archive.gz'
#>

[CmdletBinding()]
param(
    [string]$DumpFile
)

$ErrorActionPreference = 'Stop'
$sandboxRoot = Split-Path -Parent $PSScriptRoot
$backupsDir = Join-Path $sandboxRoot 'backups'

if (-not $DumpFile) {
    $latest = Get-ChildItem -Path $backupsDir -Filter 'mongo-*.archive.gz' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $latest) {
        Write-Error "No dump file given and none found in $backupsDir - run dump-mongo.ps1 first, or pass -DumpFile."
    }
    $DumpFile = $latest.FullName
}

if (-not (Test-Path $DumpFile)) {
    Write-Error "Dump file not found: $DumpFile"
}

Push-Location $sandboxRoot
try {
    # See dump-mongo.ps1's identical comment: picks whichever replica-set member is actually
    # running to exec into, rather than assuming mongo1 specifically. mongorestore needs to WRITE
    # (drop + reinsert), which only the current primary can do - the full replica-set URI below is
    # what lets the driver find that primary regardless of which container runs the command.
    $execService = 'mongo1', 'mongo2', 'mongo3' | Where-Object { docker compose ps -q $_ } | Select-Object -First 1
    if (-not $execService) {
        Write-Error 'No mongo1/mongo2/mongo3 container found/running - start the stack first.'
    }
    $containerId = docker compose ps -q $execService
    $containerTmpFile = '/tmp/mongo-restore.archive.gz'

    Write-Host "==> Restoring $DumpFile into the mongo replica set (via $execService)" -ForegroundColor Cyan
    docker cp $DumpFile "${containerId}:$containerTmpFile"
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'docker cp failed to copy the dump into the container - see output above.'
    }
    docker compose exec -T $execService mongorestore --uri="mongodb://mongo1:27017,mongo2:27017,mongo3:27017/clicks_meta_db?replicaSet=rs0" --archive="$containerTmpFile" --gzip --drop
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'mongorestore failed - see output above.'
    }
    docker compose exec -T $execService rm -f $containerTmpFile
} finally {
    Pop-Location
}

Write-Host "Restore complete." -ForegroundColor Green
