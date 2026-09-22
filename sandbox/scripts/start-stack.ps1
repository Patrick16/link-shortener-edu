<#
.SYNOPSIS
    Starts the full Link Shortener stack: the docker-compose backend (Postgres, Redis, RabbitMQ,
    and all 5 .NET services) plus the frontend dev server.

.PARAMETER Build
    Rebuild the backend Docker images before starting (use after changing backend/.NET code).

.PARAMETER SkipFrontend
    Only start the backend (docker compose); don't launch the frontend dev server.

.PARAMETER NoBrowser
    Don't automatically open a browser tab once the frontend dev server is up.

.EXAMPLE
    .\scripts\start-stack.ps1
.EXAMPLE
    .\scripts\start-stack.ps1 -Build
.EXAMPLE
    .\scripts\start-stack.ps1 -SkipFrontend
#>

[CmdletBinding()]
param(
    [switch]$Build,
    [switch]$SkipFrontend,
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'

$sandboxRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $sandboxRoot
$frontendDir = Join-Path $repoRoot 'src\frontend\app'

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
    if ($Build) {
        Write-Step 'Building backend images (docker compose build)'
        docker compose build
        if ($LASTEXITCODE -ne 0) {
            Write-Error 'docker compose build failed - see output above.'
        }
    }

    Write-Step 'Starting backend (postgres + 2 replicas, redis sentinel cluster, mongo replica set, rabbitmq, auth-api, link-api, redirect-api, shortener-service, traffic-service)'
    docker compose up -d
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

# --- Frontend: npm run dev in its own window -------------------------------------------

Write-Step 'Preparing the frontend'

$envLocal = Join-Path $frontendDir '.env.local'
$envExample = Join-Path $frontendDir '.env.example'
if (-not (Test-Path $envLocal)) {
    Copy-Item $envExample $envLocal
    Write-Host "  Created src\frontend\app\.env.local from .env.example"
}

if (-not (Test-Path (Join-Path $frontendDir 'node_modules'))) {
    Write-Step 'Installing frontend dependencies (npm install)'
    Push-Location $frontendDir
    try {
        npm install
        if ($LASTEXITCODE -ne 0) {
            Write-Error 'npm install failed - see output above.'
        }
    } finally {
        Pop-Location
    }
}

Write-Step 'Starting the frontend dev server in a new window'
Start-Process powershell -ArgumentList @(
    '-NoExit',
    '-Command',
    "Set-Location '$frontendDir'; npm run dev"
)

if (-not $NoBrowser) {
    Start-Sleep -Seconds 3
    Start-Process 'http://localhost:5173'
}

Write-Host "`nFull stack is up:" -ForegroundColor Cyan
Write-Host '  Frontend:         http://localhost:5173'
Write-Host '  AuthApi:          http://localhost:8081/scalar/v1'
Write-Host '  LinkApi:          http://localhost:8082/scalar/v1'
Write-Host '  RedirectApi:      http://localhost:8083/scalar/v1'
Write-Host '  RabbitMQ UI:      http://localhost:15672  (guest / guest)'
Write-Host '  RedisInsight:     http://localhost:5540  (add a DB: host "redis-master", port 6379)'
Write-Host '  Mongo Express:    http://localhost:8085'
Write-Host '  Aspire Dashboard: http://localhost:18888  (logs, metrics, traces)'
Write-Host "`nStop the backend with: .\stop-stack.ps1"
Write-Host "Stop the frontend by closing its window (or Ctrl+C in it)."
