<#
.SYNOPSIS
    Stops the backend (docker compose down). Postgres data survives (named volume) unless -Wipe
    is passed. Doesn't touch the frontend dev server - close its own window or Ctrl+C it.

.PARAMETER Wipe
    Also remove the Postgres data volume (fresh database next start).

.EXAMPLE
    .\scripts\stop-stack.ps1
.EXAMPLE
    .\scripts\stop-stack.ps1 -Wipe
#>

[CmdletBinding()]
param(
    [switch]$Wipe
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    if ($Wipe) {
        Write-Host "==> Stopping backend and removing volumes (Postgres data will be wiped)" -ForegroundColor Yellow
        docker compose down -v
    } else {
        Write-Host "==> Stopping backend (Postgres data kept)" -ForegroundColor Cyan
        docker compose down
    }
} finally {
    Pop-Location
}
