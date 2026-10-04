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
$sandboxRoot = Split-Path -Parent $PSScriptRoot

Push-Location $sandboxRoot
try {
    # Always activates every optional profile on `down`, regardless of which -Observability mode or
    # which -SkipXxxUi choices start-stack.ps1 was run with - a profile the run never started just
    # has nothing to stop, but without this a Full run's prometheus/jaeger/loki/grafana containers
    # (or a prior run's pgweb/RedisInsight/Mongo Express) would be left as orphans (profiles gate
    # `down` the same way they gate `up`). See docker-compose.yml for what each profile covers.
    $allProfileArgs = @('--profile', 'observability', '--profile', 'otel', '--profile', 'ui-postgres', '--profile', 'ui-redis', '--profile', 'ui-mongo', '--profile', 'ui-sqlite')
    if ($Wipe) {
        Write-Host "==> Stopping backend and removing volumes (Postgres data will be wiped)" -ForegroundColor Yellow
        docker compose @allProfileArgs down -v
    } else {
        Write-Host "==> Stopping backend (Postgres data kept)" -ForegroundColor Cyan
        docker compose @allProfileArgs down
    }
} finally {
    Pop-Location
}
