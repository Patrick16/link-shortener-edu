<#
.SYNOPSIS
    Dumps all Postgres databases (users_db, links_db, clicks_db) from the running `postgres`
    container into a timestamped .sql file under sandbox/backups/ (gitignored). The named
    Docker volume stays the source of truth for local runs; this is an on-demand snapshot for
    when you want the data as an actual file - to inspect, archive, or move to another machine.

.EXAMPLE
    .\sandbox\scripts\dump-db.ps1
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
$outFile = Join-Path $backupsDir "postgres-$timestamp.sql"

Push-Location $sandboxRoot
try {
    Write-Host "==> Dumping all databases from the postgres container" -ForegroundColor Cyan
    docker compose exec -T postgres pg_dumpall -U postgres | Out-File -FilePath $outFile -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'pg_dumpall failed - see output above.'
    }
} finally {
    Pop-Location
}

Write-Host "Dump written to: $outFile" -ForegroundColor Green
Write-Host "Restore it with: .\sandbox\scripts\restore-db.ps1 -DumpFile '$outFile'"
