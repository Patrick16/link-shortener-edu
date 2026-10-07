<#
.SYNOPSIS
    Runs dump-db.ps1, dump-mongo.ps1, and dump-clickhouse.ps1 in sequence - one command to snapshot
    every store this project writes to, each into its own timestamped file under sandbox/backups/.

.EXAMPLE
    .\sandbox\scripts\dump-all.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot

& (Join-Path $scriptDir 'dump-db.ps1')
& (Join-Path $scriptDir 'dump-mongo.ps1')
& (Join-Path $scriptDir 'dump-clickhouse.ps1')

Write-Host "==> All dumps complete." -ForegroundColor Green
