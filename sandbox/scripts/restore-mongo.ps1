<#
.SYNOPSIS
    Restores a mongodump archive (created by dump-mongo.ps1) into the running `mongo1` container.
    --drop replaces clicks_meta_db's existing collections with the dump's contents, same
    full-replace spirit as restore-db.ps1's psql replay.

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
    $containerId = docker compose ps -q mongo1
    if (-not $containerId) {
        Write-Error 'mongo1 container not found/running - start the stack first.'
    }
    $containerTmpFile = '/tmp/mongo-restore.archive.gz'

    # Full replica-set URI - mongorestore needs to WRITE (drop + reinsert), which only the current
    # primary can do, and mongo1 isn't guaranteed to be it (see dump-mongo.ps1's identical comment).
    Write-Host "==> Restoring $DumpFile into the mongo1 container" -ForegroundColor Cyan
    docker cp $DumpFile "${containerId}:$containerTmpFile"
    docker compose exec -T mongo1 mongorestore --uri="mongodb://mongo1:27017,mongo2:27017,mongo3:27017/clicks_meta_db?replicaSet=rs0" --archive="$containerTmpFile" --gzip --drop
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'mongorestore failed - see output above.'
    }
    docker compose exec -T mongo1 rm -f $containerTmpFile
} finally {
    Pop-Location
}

Write-Host "Restore complete." -ForegroundColor Green
