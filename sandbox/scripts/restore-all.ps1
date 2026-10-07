<#
.SYNOPSIS
    Runs restore-db.ps1, restore-mongo.ps1, and restore-clickhouse.ps1 in sequence, each against
    the newest dump of its own kind in sandbox/backups/ - one command to restore every store this
    project writes to. Pass explicit per-store file paths if you need anything other than "the
    latest of each" (call the three scripts directly instead).

.EXAMPLE
    .\sandbox\scripts\restore-all.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot

& (Join-Path $scriptDir 'restore-db.ps1')
& (Join-Path $scriptDir 'restore-mongo.ps1')
& (Join-Path $scriptDir 'restore-clickhouse.ps1')

Write-Host "==> All restores complete." -ForegroundColor Green
