<#
.SYNOPSIS
    Restores a reports_db.clicks Native-format dump (created by dump-clickhouse.ps1) into the
    running `clickhouse` container. Truncates the table first - same full-replace spirit as
    restore-db.ps1/restore-mongo.ps1's `--drop`, and avoids temporary duplicate rows that would
    otherwise sit there until ReplacingMergeTree's next background merge collapses them (see
    sandbox/infra/clickhouse/init.sql's own comment on that mechanism).

.PARAMETER DumpFile
    Path to the .native file to restore. Defaults to the newest clickhouse-*.native in
    sandbox/backups/.

.EXAMPLE
    .\sandbox\scripts\restore-clickhouse.ps1
.EXAMPLE
    .\sandbox\scripts\restore-clickhouse.ps1 -DumpFile '.\sandbox\backups\clickhouse-reports_db.clicks-20261007-000000.native'
#>

[CmdletBinding()]
param(
    [string]$DumpFile
)

$ErrorActionPreference = 'Stop'
$sandboxRoot = Split-Path -Parent $PSScriptRoot
$backupsDir = Join-Path $sandboxRoot 'backups'

if (-not $DumpFile) {
    $latest = Get-ChildItem -Path $backupsDir -Filter 'clickhouse-*.native' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $latest) {
        Write-Error "No dump file given and none found in $backupsDir - run dump-clickhouse.ps1 first, or pass -DumpFile."
    }
    $DumpFile = $latest.FullName
}

if (-not (Test-Path $DumpFile)) {
    Write-Error "Dump file not found: $DumpFile"
}

Push-Location $sandboxRoot
try {
    $containerId = docker compose ps -q clickhouse
    if (-not $containerId) {
        Write-Error 'clickhouse container not found/running - start the stack first.'
    }
    $containerTmpFile = '/tmp/clickhouse-restore.native'

    Write-Host "==> Restoring $DumpFile into the clickhouse container" -ForegroundColor Cyan
    docker cp $DumpFile "${containerId}:$containerTmpFile"
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'docker cp failed to copy the dump into the container - see output above.'
    }
    docker compose exec -T clickhouse clickhouse-client --query 'TRUNCATE TABLE reports_db.clicks'
    if ($LASTEXITCODE -ne 0) {
        # Without this check, PowerShell doesn't abort on a failed native-exe call (unlike a
        # terminating cmdlet error), so execution would fall through to the INSERT below and quietly
        # turn this restore into an append instead of the documented full-replace.
        Write-Error 'TRUNCATE TABLE failed - see output above. Aborting before the INSERT would have appended onto existing data.'
    }
    docker compose exec -T clickhouse sh -c "clickhouse-client --query 'INSERT INTO reports_db.clicks FORMAT Native' < $containerTmpFile"
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'clickhouse-client import failed - see output above.'
    }
    docker compose exec -T clickhouse rm -f $containerTmpFile
} finally {
    Pop-Location
}

Write-Host "Restore complete." -ForegroundColor Green
