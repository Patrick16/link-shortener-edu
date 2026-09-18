<#
.SYNOPSIS
    Restores a pg_dumpall snapshot (created by dump-db.ps1) into the running `postgres`
    container. The dump already contains CREATE DATABASE statements for users_db/links_db/
    clicks_db, so this connects to the `postgres` maintenance database to replay it.

.PARAMETER DumpFile
    Path to the .sql file to restore. Defaults to the newest file in sandbox/backups/.

.EXAMPLE
    .\sandbox\scripts\restore-db.ps1
.EXAMPLE
    .\sandbox\scripts\restore-db.ps1 -DumpFile '.\sandbox\backups\postgres-20260918-120000.sql'
#>

[CmdletBinding()]
param(
    [string]$DumpFile
)

$ErrorActionPreference = 'Stop'
$sandboxRoot = Split-Path -Parent $PSScriptRoot
$backupsDir = Join-Path $sandboxRoot 'backups'

if (-not $DumpFile) {
    $latest = Get-ChildItem -Path $backupsDir -Filter '*.sql' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $latest) {
        Write-Error "No dump file given and none found in $backupsDir - run dump-db.ps1 first, or pass -DumpFile."
    }
    $DumpFile = $latest.FullName
}

if (-not (Test-Path $DumpFile)) {
    Write-Error "Dump file not found: $DumpFile"
}

Push-Location $sandboxRoot
try {
    Write-Host "==> Restoring $DumpFile into the postgres container" -ForegroundColor Cyan
    Get-Content -Raw $DumpFile | docker compose exec -T postgres psql -U postgres -d postgres
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'psql restore failed - see output above.'
    }
} finally {
    Pop-Location
}

Write-Host "Restore complete." -ForegroundColor Green
