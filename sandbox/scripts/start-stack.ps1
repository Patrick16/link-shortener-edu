<#
.SYNOPSIS
    Starts the full Link Shortener stack: the docker-compose backend (Postgres, Redis, RabbitMQ,
    and all 5 .NET services) plus both frontend dev servers (the product app and the sandbox
    architecture-map).

.PARAMETER Build
    Rebuild the backend Docker images before starting (use after changing backend/.NET code).

.PARAMETER SkipFrontend
    Don't launch either frontend dev server (product app or architecture-map) at all. Finer-grained
    control over the product app alone is -SkipProductUi; architecture-map (the sandbox's own
    control-plane UI, not part of the product) has no toggle - it always starts whenever frontends
    aren't skipped altogether, since it's the panel you drive the rest of this script's choices from.

.PARAMETER NoBrowser
    Don't automatically open a browser tab once the frontend dev servers are up.

.PARAMETER Observability
    Which telemetry stack to route traces/metrics/logs to: 'Aspire' (the lightweight Aspire
    Dashboard, fine for everyday dev), 'Full' (default - Prometheus + Jaeger + Loki + Grafana, for
    load-test runs since Aspire Dashboard's in-memory store chokes under that volume), or 'None'
    (don't start otel-collector/aspire-dashboard/the Full stack at all - services still run, they
    just drop their telemetry on the floor). Skips the interactive prompt below when passed.

.PARAMETER SkipPostgresUi
    Don't start pgweb (the Postgres browser UI, port 8084).

.PARAMETER SkipRedisUi
    Don't start RedisInsight (port 5540).

.PARAMETER SkipMongoUi
    Don't start Mongo Express (port 8085).

.PARAMETER SkipSqliteUi
    Don't start the two sqlite-web fallback viewers (link-api-fallback-viewer port 8086,
    redirect-api-fallback-viewer port 8087).

.PARAMETER SkipRabbitMqUi
    Don't expose RabbitMQ's management UI (port 15672) on the host. The broker itself (AMQP,
    Prometheus metrics) is unaffected - this only toggles whether that one port is published,
    since the UI lives in the same container as the broker, not a separate one.

.PARAMETER SkipProductUi
    Don't start the product app's own dev server (src/frontend/app, port 5173). architecture-map
    still starts (see -SkipFrontend above).

.EXAMPLE
    .\scripts\start-stack.ps1
.EXAMPLE
    .\scripts\start-stack.ps1 -Build
.EXAMPLE
    .\scripts\start-stack.ps1 -SkipFrontend
.EXAMPLE
    .\scripts\start-stack.ps1 -Observability Aspire -SkipMongoUi -SkipProductUi
#>

[CmdletBinding()]
param(
    [switch]$Build,
    [switch]$SkipFrontend,
    [switch]$NoBrowser,
    [ValidateSet('Aspire', 'Full', 'None')]
    [string]$Observability,
    [switch]$SkipPostgresUi,
    [switch]$SkipRedisUi,
    [switch]$SkipMongoUi,
    [switch]$SkipSqliteUi,
    [switch]$SkipRabbitMqUi,
    [switch]$SkipProductUi
)

$ErrorActionPreference = 'Stop'

function Read-MenuChoice {
    param(
        [string]$Title,
        [string[]]$Options,  # each "label (description)" in display order; index 0 is the default
        [string]$Prompt
    )
    Write-Host "`n$Title" -ForegroundColor Cyan
    for ($i = 0; $i -lt $Options.Count; $i++) {
        $suffix = if ($i -eq 0) { ' (default)' } else { '' }
        Write-Host "  [$($i + 1)] $($Options[$i])$suffix"
    }
    $raw = Read-Host "$Prompt (1-$($Options.Count), Enter = 1)"
    $index = 0
    if ($raw -and [int]::TryParse($raw, [ref]$index) -and $index -ge 1 -and $index -le $Options.Count) {
        return $index - 1
    }
    return 0
}

function Read-YesNo {
    param(
        [string]$Prompt,
        [bool]$DefaultYes = $true
    )
    $suffix = if ($DefaultYes) { 'Y/n' } else { 'y/N' }
    $raw = Read-Host "$Prompt ($suffix)"
    if (-not $raw) { return $DefaultYes }
    return $raw -match '^(y|yes|д|да)$'
}

if (-not $Observability) {
    $choice = Read-MenuChoice `
        -Title 'Which observability stack should this run route telemetry to?' `
        -Options @(
            'Full: Prometheus + Jaeger + Loki + Grafana (for load-test runs - Aspire Dashboard chokes on that volume)'
            'Aspire Dashboard only (lightweight, fine for everyday dev)'
            "None (don't start otel-collector/aspire-dashboard at all - services just drop telemetry)"
        ) `
        -Prompt 'Choice'
    $Observability = @('Full', 'Aspire', 'None')[$choice]
}

# -SkipXxxUi switches are either present or absent - PSBoundParameters is how we tell "explicitly
# passed -SkipXxxUi:$false" (still absent unless the user wrote exactly that) from "never mentioned,
# ask interactively". Each of these UI containers is independently profile-gated in
# docker-compose.yml (ui-postgres/ui-redis/ui-mongo) specifically so this script can include or
# leave out just that profile instead of needing to list every other service by name.
if (-not $PSBoundParameters.ContainsKey('SkipPostgresUi')) {
    $SkipPostgresUi = -not (Read-YesNo -Prompt 'Start the Postgres UI (pgweb, port 8084)?')
}
if (-not $PSBoundParameters.ContainsKey('SkipRedisUi')) {
    $SkipRedisUi = -not (Read-YesNo -Prompt 'Start the Redis UI (RedisInsight, port 5540)?')
}
if (-not $PSBoundParameters.ContainsKey('SkipMongoUi')) {
    $SkipMongoUi = -not (Read-YesNo -Prompt 'Start the Mongo UI (Mongo Express, port 8085)?')
}
if (-not $PSBoundParameters.ContainsKey('SkipSqliteUi')) {
    $SkipSqliteUi = -not (Read-YesNo -Prompt 'Start the SQLite fallback-queue UIs (ports 8086/8087)?')
}
if (-not $PSBoundParameters.ContainsKey('SkipRabbitMqUi')) {
    $SkipRabbitMqUi = -not (Read-YesNo -Prompt 'Start the RabbitMQ UI (management UI, port 15672)?')
}
if (-not $SkipFrontend -and -not $PSBoundParameters.ContainsKey('SkipProductUi')) {
    $SkipProductUi = -not (Read-YesNo -Prompt 'Start the product UI (src/frontend/app, port 5173)?')
}

$sandboxRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $sandboxRoot
$frontendDir = Join-Path $repoRoot 'src\frontend\app'
$architectureMapDir = Join-Path $sandboxRoot 'frontend\architecture-map'

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Test-CommandExists {
    param([string]$Name)
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

function Wait-ForHealthReady {
    param(
        [string]$ComputerName = 'localhost',
        [int]$Port,
        [int]$TimeoutSeconds = 60
    )
    $uri = "http://${ComputerName}:$Port/health/ready"
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -eq 200) {
                return $true
            }
        } catch {
            # Not up yet, or /health/ready is reporting unhealthy - keep polling.
        }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Test-PortInUse {
    param([int]$Port)
    return [bool](Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
}

function Wait-ForDevServer {
    param(
        [string]$Url,
        [int]$TimeoutSeconds = 20
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) {
                return $true
            }
        } catch {
            # Not up yet (Vite still cold-starting), or the window failed to launch at all - keep
            # polling either way until the timeout.
        }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Wait-ForHealthyContainer {
    param(
        [string]$ServiceName,
        [int]$TimeoutSeconds = 60
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $status = docker compose ps $ServiceName --format json | ConvertFrom-Json
            if ($status.Health -eq 'healthy') {
                return $true
            }
        } catch {
            # docker compose ps failed transiently (e.g. container not created yet) - keep polling.
        }
        Start-Sleep -Seconds 2
    }
    return $false
}

# --- Prerequisites -------------------------------------------------------------------

Write-Step 'Checking prerequisites'

if (-not (Test-CommandExists 'docker')) {
    Write-Error "Docker isn't on PATH. Install Docker Desktop and try again."
}

docker info *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Error 'Docker daemon is not running. Start Docker Desktop and try again.'
}

if (-not $SkipFrontend -and -not (Test-CommandExists 'npm')) {
    Write-Error "npm isn't on PATH. Install Node.js, or pass -SkipFrontend to start only the backend."
}

# --- Backend: docker compose ----------------------------------------------------------

Push-Location $sandboxRoot
try {
    # pgcat.toml is live-mutable at runtime (control-api's pgcat pool-settings control rewrites it
    # directly - see NodePanel's pgcat panel), so it's gitignored and only the baseline is tracked,
    # same .example/local-copy pattern as the frontends' .env.local below. Must happen before
    # `docker compose up`, since pgcat's bind mount needs the file to already exist.
    $pgcatConfig = Join-Path $sandboxRoot 'infra\pgcat\pgcat.toml'
    $pgcatConfigExample = Join-Path $sandboxRoot 'infra\pgcat\pgcat.toml.example'
    if ((Test-Path $pgcatConfigExample) -and -not (Test-Path $pgcatConfig)) {
        Copy-Item $pgcatConfigExample $pgcatConfig
        Write-Host '  Created infra\pgcat\pgcat.toml from pgcat.toml.example'
    }

    if ($Build) {
        Write-Step 'Building backend images (docker compose build)'
        docker compose build
        if ($LASTEXITCODE -ne 0) {
            Write-Error 'docker compose build failed - see output above.'
        }
    }

    # Each optional extra (observability's two tiers, each UI viewer) is its own Compose profile
    # (see docker-compose.yml) - the service names list below is only used to proactively `stop`
    # whichever ones this run opted OUT of, so a leftover container from a *previous* run (e.g. a
    # prior -Observability Full, or a prior "yes" to the Mongo UI prompt) doesn't linger as an
    # orphan just because this run's `docker compose up` never mentions its profile.
    $profileServices = [ordered]@{
        otel            = @('aspire-dashboard', 'otel-collector')
        observability   = @('prometheus', 'jaeger', 'loki', 'grafana')
        'ui-postgres'   = @('pgweb')
        'ui-redis'      = @('redisinsight')
        'ui-mongo'      = @('mongo-express')
        'ui-sqlite'     = @('link-api-fallback-viewer', 'redirect-api-fallback-viewer')
    }

    $activeProfiles = New-Object System.Collections.Generic.List[string]
    if ($Observability -ne 'None') { $activeProfiles.Add('otel') }
    if ($Observability -eq 'Full') { $activeProfiles.Add('observability') }
    if (-not $SkipPostgresUi) { $activeProfiles.Add('ui-postgres') }
    if (-not $SkipRedisUi) { $activeProfiles.Add('ui-redis') }
    if (-not $SkipMongoUi) { $activeProfiles.Add('ui-mongo') }
    if (-not $SkipSqliteUi) { $activeProfiles.Add('ui-sqlite') }

    $inactiveServices = @(
        $profileServices.Keys | Where-Object { $activeProfiles -notcontains $_ } |
            ForEach-Object { $profileServices[$_] }
    )
    if ($inactiveServices.Count -gt 0) {
        docker compose stop @inactiveServices 2>$null
    }

    if ($Observability -eq 'Full') {
        $env:OTEL_COLLECTOR_CONFIG = 'otel-collector-config.full.yaml'
    } else {
        # $env: assignments land on this whole PowerShell process, not just this script run -
        # without an explicit reset here, a prior `-Observability Full` invocation *in the same
        # window* would leave OTEL_COLLECTOR_CONFIG pointing at the Full config forever, silently
        # breaking a later Aspire-mode otel-collector even though that run never asked for Full.
        Remove-Item Env:\OTEL_COLLECTOR_CONFIG -ErrorAction SilentlyContinue
    }

    # RabbitMQ's management UI lives in the same container as the broker (unlike pgweb/
    # RedisInsight/Mongo Express, which are separate containers gated by their own profile above),
    # so it's toggled by publishing or not publishing its one host port instead - see
    # docker-compose.yml's `${RABBITMQ_UI_PORT-15672}:15672` for why this must be an *explicit*
    # empty string (not just unset) to actually suppress it, and why that line uses bash's `-`
    # default-if-unset instead of `:-` default-if-unset-or-empty.
    if ($SkipRabbitMqUi) {
        $env:RABBITMQ_UI_PORT = ''
    } else {
        Remove-Item Env:\RABBITMQ_UI_PORT -ErrorAction SilentlyContinue
    }

    $profileArgs = @($activeProfiles | ForEach-Object { '--profile', $_ })
    Write-Step "Starting backend (observability: $Observability; profiles: $(if ($activeProfiles.Count -gt 0) { $activeProfiles -join ', ' } else { 'none' }))"
    docker compose @profileArgs up -d
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'docker compose up failed - see output above.'
    }

    Write-Step 'Waiting for the web APIs to become ready (/health/ready)'
    $apis = @(
        @{ Name = 'AuthApi';     Port = 8081 }
        @{ Name = 'LinkApi';     Port = 8082 }
        @{ Name = 'RedirectApi'; Port = 8083 }
    )
    foreach ($api in $apis) {
        if (Wait-ForHealthReady -Port $api.Port) {
            Write-Host "  $($api.Name) is ready" -ForegroundColor Green
        } else {
            Write-Warning "  $($api.Name) didn't become ready within the timeout - check 'docker compose logs $($api.Name.ToLower())'"
        }
    }

    Write-Step 'Waiting for the worker services to become healthy'
    $workers = @('shortener-service', 'traffic-service')
    foreach ($worker in $workers) {
        if (Wait-ForHealthyContainer -ServiceName $worker) {
            Write-Host "  $worker is healthy" -ForegroundColor Green
        } else {
            Write-Warning "  $worker didn't become healthy within the timeout - check 'docker compose logs $worker'"
        }
    }
} finally {
    Pop-Location
}

if ($SkipFrontend) {
    Write-Host "`nBackend is up. Stop it later with: .\stop-stack.ps1" -ForegroundColor Cyan
    exit 0
}

# --- Frontends: npm run dev, each in its own window --------------------------------------

Write-Step 'Preparing the frontends'

function Initialize-Frontend {
    param(
        [string]$Dir,
        [string]$Label
    )

    $envLocal = Join-Path $Dir '.env.local'
    $envExample = Join-Path $Dir '.env.example'
    if ((Test-Path $envExample) -and -not (Test-Path $envLocal)) {
        Copy-Item $envExample $envLocal
        Write-Host "  Created $Label\.env.local from .env.example"
    }

    if (-not (Test-Path (Join-Path $Dir 'node_modules'))) {
        Write-Step "Installing $Label dependencies (npm install)"
        Push-Location $Dir
        try {
            npm install
            if ($LASTEXITCODE -ne 0) {
                Write-Error "npm install failed for $Label - see output above."
            }
        } finally {
            Pop-Location
        }
    }
}

if (-not $SkipProductUi) {
    Initialize-Frontend -Dir $frontendDir -Label 'src\frontend\app'
}
Initialize-Frontend -Dir $architectureMapDir -Label 'sandbox\frontend\architecture-map'

Write-Step 'Starting the frontend dev servers, each in a new window'

# The product app's dev server has no fixed port (Vite auto-increments past a busy 5173), but
# architecture-map's vite.config.ts pins port 5174 with strictPort - so it's the one that actually
# fails outright if anything (a leftover window from a previous run, or the product app itself
# auto-incrementing onto it) is already listening there. Checked before spawning either window,
# not after, so a doomed-to-fail launch is skipped instead of opening a window that immediately
# crashes with no indication in this script's own output.
if (-not $SkipProductUi -and (Test-PortInUse -Port 5173)) {
    Write-Warning "  Port 5173 is already in use - a previous 'npm run dev' window for the product app may still be running. Its dev server will auto-pick the next free port instead of failing outright, but that can land it on 5174 and collide with architecture-map below."
}

$architectureMapPortFree = -not (Test-PortInUse -Port 5174)
if (-not $architectureMapPortFree) {
    Write-Warning "  Port 5174 is already in use - architecture-map's dev server pins this exact port (strictPort) and will fail to start if it's taken, most likely by a previous 'npm run dev' window that was never closed. Close it and re-run this script; skipping the architecture-map window for now."
}

if (-not $SkipProductUi) {
    Start-Process powershell -ArgumentList @(
        '-NoExit',
        '-Command',
        "Set-Location '$frontendDir'; npm run dev"
    )
}

if ($architectureMapPortFree) {
    Start-Process powershell -ArgumentList @(
        '-NoExit',
        '-Command',
        "Set-Location '$architectureMapDir'; npm run dev"
    )
}

Write-Step 'Waiting for the frontend dev servers to actually come up'
$productUp = $false
if (-not $SkipProductUi) {
    $productUp = Wait-ForDevServer -Url 'http://localhost:5173'
    if ($productUp) {
        Write-Host '  Frontend (product) is up' -ForegroundColor Green
    } else {
        Write-Warning "  Frontend (product) didn't respond within the timeout - check its own window for errors."
    }
}

$sandboxMapUp = $false
if ($architectureMapPortFree) {
    $sandboxMapUp = Wait-ForDevServer -Url 'http://localhost:5174'
    if ($sandboxMapUp) {
        Write-Host '  Frontend (sandbox map) is up' -ForegroundColor Green
    } else {
        Write-Warning "  Frontend (sandbox map) didn't respond within the timeout - check its own window for errors."
    }
}

if (-not $NoBrowser) {
    # Only open a tab for whichever dev server was actually confirmed up above - opening a tab
    # against a dev server that never bound its port (or was skipped for a port conflict, or
    # skipped by choice via -SkipProductUi) just shows the browser's own connection-refused page,
    # no more informative than the warnings above.
    if ($productUp) {
        Start-Process 'http://localhost:5173'
    }
    if ($sandboxMapUp) {
        Start-Process 'http://localhost:5174'
    }
}

Write-Host "`nFull stack is up:" -ForegroundColor Cyan
if (-not $SkipProductUi) {
    Write-Host "  Frontend (product):     http://localhost:5173$(if (-not $productUp) { '  (not confirmed up - see warning above)' })"
} else {
    Write-Host '  Frontend (product):     skipped (-SkipProductUi)'
}
Write-Host "  Frontend (sandbox map): http://localhost:5174$(if (-not $sandboxMapUp) { '  (not confirmed up - see warning above)' })"
Write-Host '  AuthApi:          http://localhost:8081/scalar/v1'
Write-Host '  LinkApi:          http://localhost:8082/scalar/v1'
Write-Host '  RedirectApi:      http://localhost:8083/scalar/v1'
if (-not $SkipRabbitMqUi) {
    Write-Host '  RabbitMQ UI:      http://localhost:15672  (guest / guest)'
}
if (-not $SkipRedisUi) {
    Write-Host '  RedisInsight:     http://localhost:5540  (add a DB: host "redis-master", port 6379)'
}
if (-not $SkipPostgresUi) {
    Write-Host '  pgweb:            http://localhost:8084'
}
if (-not $SkipMongoUi) {
    Write-Host '  Mongo Express:    http://localhost:8085'
}
if (-not $SkipSqliteUi) {
    Write-Host '  LinkApi fallback queue (sqlite-web):     http://localhost:8086'
    Write-Host '  RedirectApi fallback queue (sqlite-web): http://localhost:8087'
}
switch ($Observability) {
    'Full' {
        Write-Host '  Grafana:          http://localhost:3000  (Prometheus/Jaeger/Loki pre-wired)'
        Write-Host '  Prometheus:       http://localhost:9090'
        Write-Host '  Jaeger:           http://localhost:16686'
        Write-Host '  Loki:             http://localhost:3100  (query via Grafana Explore, not a browsable UI)'
        Write-Host '  Aspire Dashboard: http://localhost:18888  (up, but not receiving telemetry in Full mode)'
    }
    'Aspire' {
        Write-Host '  Aspire Dashboard: http://localhost:18888  (logs, metrics, traces)'
    }
    'None' {
        Write-Host '  Observability:    none - otel-collector/aspire-dashboard are not running, services drop their telemetry.'
    }
}
Write-Host "`nStop the backend with: .\stop-stack.ps1"
Write-Host "Stop the frontends by closing their windows (or Ctrl+C in each)."
