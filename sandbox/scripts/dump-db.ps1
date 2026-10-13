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
    $dump = docker compose exec -T postgres pg_dumpall -U postgres | Out-String
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'pg_dumpall failed - see output above.'
    }

    # Out-File -Encoding utf8 adds a BOM on Windows PowerShell 5.1 (fixed in PowerShell 7, but this
    # script has to run on both) - a BOM at the start of the file makes psql choke on restore. The
    # fix (-Encoding utf8NoBOM) isn't a valid value on 5.1 at all, so writing via the .NET API
    # directly is the one approach that's actually BOM-free on both.
    [System.IO.File]::WriteAllText($outFile, $dump, [System.Text.UTF8Encoding]::new($false))
} finally {
    Pop-Location
}

Write-Host "Dump written to: $outFile" -ForegroundColor Green
Write-Host "Restore it with: .\sandbox\scripts\restore-db.ps1 -DumpFile '$outFile'"
