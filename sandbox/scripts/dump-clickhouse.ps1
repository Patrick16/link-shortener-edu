<#
.SYNOPSIS
    Dumps reports_db.clicks from the running `clickhouse` container into a timestamped .native file
    under sandbox/backups/ (gitignored) - ClickHouse's own Native format, the simplest
    round-trippable export with no server-side backup-disk configuration needed (unlike
    ClickHouse's native BACKUP/RESTORE SQL commands, which this project doesn't set up). Same
    on-demand-snapshot role as dump-db.ps1, just for ClickHouse instead of Postgres. Only one table
    exists today (reports_db.clicks) - this stays a single-table dump rather than a generic
    whole-database one until there's a second table to justify the extra complexity.

.EXAMPLE
    .\sandbox\scripts\dump-clickhouse.ps1
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
$outFile = Join-Path $backupsDir "clickhouse-reports_db.clicks-$timestamp.native"
# Native format output - written inside the container first and pulled out with `docker cp`,
# rather than redirecting the container's stdout from PowerShell, which mangles binary data
# through its text-pipeline encoding (see dump-mongo.ps1's identical reasoning).
$containerTmpFile = "/tmp/clickhouse-clicks-$timestamp.native"

Push-Location $sandboxRoot
try {
    $containerId = docker compose ps -q clickhouse
    if (-not $containerId) {
        Write-Error 'clickhouse container not found/running - start the stack first.'
    }

    Write-Host "==> Dumping reports_db.clicks from the clickhouse container" -ForegroundColor Cyan
    docker compose exec -T clickhouse sh -c "clickhouse-client --query 'SELECT * FROM reports_db.clicks FORMAT Native' > $containerTmpFile"
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'clickhouse-client export failed - see output above.'
    }

    docker cp "${containerId}:$containerTmpFile" $outFile
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'docker cp failed to pull the dump out of the container - see output above.'
    }
    docker compose exec -T clickhouse rm -f $containerTmpFile
} finally {
    Pop-Location
}

Write-Host "Dump written to: $outFile" -ForegroundColor Green
Write-Host "Restore it with: .\sandbox\scripts\restore-clickhouse.ps1 -DumpFile '$outFile'"
