#!/usr/bin/env bash
# Linux/macOS equivalent of start-stack.ps1 - see that file's header for the full rationale behind
# each step; this mirrors its behavior rather than re-explaining it. Kept as a parallel script
# rather than a cross-platform rewrite of the .ps1, since PowerShell constructs here (Start-Process
# opening a new window, Get-NetTCPConnection, ConvertFrom-Json on `docker compose ps`) don't have
# a 1:1 bash equivalent - see this file's own comments for how each is approximated instead.
#
# Usage:
#   ./start-stack.sh [--build] [--skip-frontend] [--no-browser]
#                     [--observability Aspire|Full|None]
#                     [--skip-postgres-ui] [--skip-redis-ui] [--skip-mongo-ui]
#                     [--skip-sqlite-ui] [--skip-rabbitmq-ui] [--skip-product-ui]
#
# Any flag left unset is asked for interactively (same prompts/defaults as start-stack.ps1).

set -euo pipefail

BUILD=0
SKIP_FRONTEND=0
NO_BROWSER=0
OBSERVABILITY=""
SKIP_POSTGRES_UI=""
SKIP_REDIS_UI=""
SKIP_MONGO_UI=""
SKIP_SQLITE_UI=""
SKIP_RABBITMQ_UI=""
SKIP_PRODUCT_UI=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --build) BUILD=1; shift ;;
        --skip-frontend) SKIP_FRONTEND=1; shift ;;
        --no-browser) NO_BROWSER=1; shift ;;
        --observability) OBSERVABILITY="$2"; shift 2 ;;
        --skip-postgres-ui) SKIP_POSTGRES_UI=1; shift ;;
        --skip-redis-ui) SKIP_REDIS_UI=1; shift ;;
        --skip-mongo-ui) SKIP_MONGO_UI=1; shift ;;
        --skip-sqlite-ui) SKIP_SQLITE_UI=1; shift ;;
        --skip-rabbitmq-ui) SKIP_RABBITMQ_UI=1; shift ;;
        --skip-product-ui) SKIP_PRODUCT_UI=1; shift ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

step() { printf '\n==> %s\n' "$1"; }
warn() { printf 'Warning: %s\n' "$1" >&2; }
die() { printf 'Error: %s\n' "$1" >&2; exit 1; }

read_yes_no() {
    # $1 = prompt, $2 = 1 if default is "yes"
    local prompt="$1" default_yes="$2" raw
    local suffix="Y/n"; [[ "$default_yes" == "0" ]] && suffix="y/N"
    read -r -p "$prompt ($suffix) " raw || true
    if [[ -z "$raw" ]]; then
        [[ "$default_yes" == "1" ]] && return 0 || return 1
    fi
    [[ "$raw" =~ ^([yY]|[yY][eE][sS]|д|да|Да|ДА)$ ]]
}

if [[ -z "$OBSERVABILITY" ]]; then
    printf '\nWhich observability stack should this run route telemetry to?\n'
    printf '  [1] Full: Prometheus + Jaeger + Loki + Grafana (default - for load-test runs, Aspire Dashboard chokes on that volume)\n'
    printf '  [2] Aspire Dashboard only (lightweight, fine for everyday dev)\n'
    printf "  [3] None (don't start otel-collector/aspire-dashboard at all - services just drop telemetry)\n"
    read -r -p 'Choice (1-3, Enter = 1): ' choice || true
    case "$choice" in
        2) OBSERVABILITY="Aspire" ;;
        3) OBSERVABILITY="None" ;;
        *) OBSERVABILITY="Full" ;;
    esac
fi
case "$OBSERVABILITY" in
    Aspire|Full|None) ;;
    *) die "Invalid --observability value: $OBSERVABILITY (expected Aspire, Full, or None)" ;;
esac

if [[ -z "$SKIP_POSTGRES_UI" ]]; then
    read_yes_no 'Start the Postgres UI (pgweb, port 8084)?' 1 && SKIP_POSTGRES_UI=0 || SKIP_POSTGRES_UI=1
fi
if [[ -z "$SKIP_REDIS_UI" ]]; then
    read_yes_no 'Start the Redis UI (RedisInsight, port 5540)?' 1 && SKIP_REDIS_UI=0 || SKIP_REDIS_UI=1
fi
if [[ -z "$SKIP_MONGO_UI" ]]; then
    read_yes_no 'Start the Mongo UI (Mongo Express, port 8085)?' 1 && SKIP_MONGO_UI=0 || SKIP_MONGO_UI=1
fi
if [[ -z "$SKIP_SQLITE_UI" ]]; then
    read_yes_no 'Start the SQLite fallback-queue UIs (ports 8086/8087)?' 1 && SKIP_SQLITE_UI=0 || SKIP_SQLITE_UI=1
fi
if [[ -z "$SKIP_RABBITMQ_UI" ]]; then
    read_yes_no 'Start the RabbitMQ UI (management UI, port 15672)?' 1 && SKIP_RABBITMQ_UI=0 || SKIP_RABBITMQ_UI=1
fi
if [[ "$SKIP_FRONTEND" == "0" && -z "$SKIP_PRODUCT_UI" ]]; then
    read_yes_no 'Start the product UI (src/frontend/app, port 5173)?' 1 && SKIP_PRODUCT_UI=0 || SKIP_PRODUCT_UI=1
fi
SKIP_POSTGRES_UI="${SKIP_POSTGRES_UI:-0}"
SKIP_REDIS_UI="${SKIP_REDIS_UI:-0}"
SKIP_MONGO_UI="${SKIP_MONGO_UI:-0}"
SKIP_SQLITE_UI="${SKIP_SQLITE_UI:-0}"
SKIP_RABBITMQ_UI="${SKIP_RABBITMQ_UI:-0}"
SKIP_PRODUCT_UI="${SKIP_PRODUCT_UI:-0}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SANDBOX_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$SANDBOX_ROOT/.." && pwd)"
FRONTEND_DIR="$REPO_ROOT/src/frontend/app"
ARCHITECTURE_MAP_DIR="$SANDBOX_ROOT/frontend/architecture-map"
LOGS_DIR="$SANDBOX_ROOT/logs"

port_in_use() {
    # No single built-in has both ss/lsof everywhere, so probe the port directly via bash's own
    # /dev/tcp pseudo-device - works on bash without any extra tool installed.
    (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null && { exec 3>&-; return 0; } || return 1
}

wait_for_health_ready() {
    # $1 = port, $2 = timeout seconds
    local port="$1" timeout="${2:-60}" deadline
    deadline=$(( $(date +%s) + timeout ))
    while [[ $(date +%s) -lt $deadline ]]; do
        if curl -fsS -o /dev/null --max-time 3 "http://localhost:$port/health/ready" 2>/dev/null; then
            return 0
        fi
        sleep 2
    done
    return 1
}

wait_for_dev_server() {
    local url="$1" timeout="${2:-20}" deadline
    deadline=$(( $(date +%s) + timeout ))
    while [[ $(date +%s) -lt $deadline ]]; do
        if curl -fsS -o /dev/null --max-time 2 "$url" 2>/dev/null; then
            return 0
        fi
        sleep 1
    done
    return 1
}

wait_for_healthy_container() {
    # $1 = compose service name, $2 = timeout seconds
    local service="$1" timeout="${2:-60}" deadline health
    deadline=$(( $(date +%s) + timeout ))
    while [[ $(date +%s) -lt $deadline ]]; do
        health=$(docker compose ps "$service" --format json 2>/dev/null | sed -n 's/.*"Health":"\([^"]*\)".*/\1/p' | head -n1 || true)
        [[ "$health" == "healthy" ]] && return 0
        sleep 2
    done
    return 1
}

# --- Prerequisites -------------------------------------------------------------------

step 'Checking prerequisites'

command -v docker >/dev/null 2>&1 || die "Docker isn't on PATH. Install Docker and try again."
docker info >/dev/null 2>&1 || die 'Docker daemon is not running. Start it and try again.'
if [[ "$SKIP_FRONTEND" == "0" ]] && ! command -v npm >/dev/null 2>&1; then
    die "npm isn't on PATH. Install Node.js, or pass --skip-frontend to start only the backend."
fi

# --- Backend: docker compose ----------------------------------------------------------

cd "$SANDBOX_ROOT"

# pgcat.toml is live-mutable at runtime (control-api's pool-settings control rewrites it directly),
# so it's gitignored and only the baseline is tracked, same .example/local-copy pattern as the
# frontends' .env.local below. Must happen before `docker compose up` - pgcat's bind mount needs
# the file to already exist.
PGCAT_CONFIG="$SANDBOX_ROOT/infra/pgcat/pgcat.toml"
PGCAT_CONFIG_EXAMPLE="$SANDBOX_ROOT/infra/pgcat/pgcat.toml.example"
if [[ -f "$PGCAT_CONFIG_EXAMPLE" && ! -f "$PGCAT_CONFIG" ]]; then
    cp "$PGCAT_CONFIG_EXAMPLE" "$PGCAT_CONFIG"
    echo '  Created infra/pgcat/pgcat.toml from pgcat.toml.example'
fi

if [[ "$BUILD" == "1" ]]; then
    step 'Building backend images (docker compose build)'
    docker compose build || die 'docker compose build failed - see output above.'
fi

# Each optional extra (observability's two tiers, each UI viewer) is its own Compose profile (see
# docker-compose.yml). We proactively `stop` the services behind whichever profiles this run opted
# OUT of, so a leftover container from a previous run (e.g. a prior --observability Full, or a
# prior "yes" to the Mongo UI prompt) doesn't linger as an orphan just because this run's
# `docker compose up` never mentions its profile.
declare -A PROFILE_SERVICES=(
    [otel]="aspire-dashboard otel-collector"
    [observability]="prometheus jaeger loki grafana"
    [ui-postgres]="pgweb"
    [ui-redis]="redisinsight"
    [ui-mongo]="mongo-express"
    [ui-sqlite]="link-api-fallback-viewer redirect-api-fallback-viewer"
)

active_profiles=()
[[ "$OBSERVABILITY" != "None" ]] && active_profiles+=("otel")
[[ "$OBSERVABILITY" == "Full" ]] && active_profiles+=("observability")
[[ "$SKIP_POSTGRES_UI" == "0" ]] && active_profiles+=("ui-postgres")
[[ "$SKIP_REDIS_UI" == "0" ]] && active_profiles+=("ui-redis")
[[ "$SKIP_MONGO_UI" == "0" ]] && active_profiles+=("ui-mongo")
[[ "$SKIP_SQLITE_UI" == "0" ]] && active_profiles+=("ui-sqlite")

is_active_profile() {
    local p
    for p in "${active_profiles[@]}"; do [[ "$p" == "$1" ]] && return 0; done
    return 1
}

inactive_services=()
for profile in "${!PROFILE_SERVICES[@]}"; do
    if ! is_active_profile "$profile"; then
        # shellcheck disable=SC2206
        inactive_services+=(${PROFILE_SERVICES[$profile]})
    fi
done
if [[ ${#inactive_services[@]} -gt 0 ]]; then
    docker compose stop "${inactive_services[@]}" 2>/dev/null || true
fi

if [[ "$OBSERVABILITY" == "Full" ]]; then
    export OTEL_COLLECTOR_CONFIG="otel-collector-config.full.yaml"
else
    # Exported env vars don't leak across shell invocations the way PowerShell's $env: does across
    # a window, but unset explicitly anyway so a sourced/re-run of this script in the same shell
    # can't inherit a stale Full setting.
    unset OTEL_COLLECTOR_CONFIG || true
fi

# RabbitMQ's management UI lives in the same container as the broker (unlike pgweb/RedisInsight/
# Mongo Express, which are separate containers gated by their own profile above), so it's toggled
# by publishing or not publishing its one host port instead - see docker-compose.yml's
# `${RABBITMQ_UI_PORT-15672}:15672` for why this must be an *explicit* empty string (not just
# unset) to actually suppress it, and why that line uses `-` (default-if-unset) rather than `:-`
# (default-if-unset-or-empty).
if [[ "$SKIP_RABBITMQ_UI" == "1" ]]; then
    export RABBITMQ_UI_PORT=""
else
    unset RABBITMQ_UI_PORT || true
fi

profile_args=()
for p in "${active_profiles[@]}"; do profile_args+=("--profile" "$p"); done

if [[ ${#active_profiles[@]} -gt 0 ]]; then
    profiles_desc=$(IFS=', '; echo "${active_profiles[*]}")
else
    profiles_desc="none"
fi
step "Starting backend (observability: $OBSERVABILITY; profiles: $profiles_desc)"
docker compose "${profile_args[@]}" up -d || die 'docker compose up failed - see output above.'

step 'Waiting for the web APIs to become ready (/health/ready)'
declare -A API_PORTS=([AuthApi]=8081 [LinkApi]=8082 [RedirectApi]=8083)
for name in AuthApi LinkApi RedirectApi; do
    if wait_for_health_ready "${API_PORTS[$name]}"; then
        echo "  $name is ready"
    else
        warn "  $name didn't become ready within the timeout - check 'docker compose logs $(echo "$name" | tr 'A-Z' 'a-z')'"
    fi
done

step 'Waiting for the worker services to become healthy'
for worker in shortener-service traffic-service; do
    if wait_for_healthy_container "$worker"; then
        echo "  $worker is healthy"
    else
        warn "  $worker didn't become healthy within the timeout - check 'docker compose logs $worker'"
    fi
done

if [[ "$SKIP_FRONTEND" == "1" ]]; then
    printf '\nBackend is up. Stop it later with: ./stop-stack.sh\n'
    exit 0
fi

# --- Frontends: npm run dev, each in its own terminal (best effort) ----------------------

step 'Preparing the frontends'

init_frontend() {
    local dir="$1" label="$2"
    if [[ -f "$dir/.env.example" && ! -f "$dir/.env.local" ]]; then
        cp "$dir/.env.example" "$dir/.env.local"
        echo "  Created $label/.env.local from .env.example"
    fi
    if [[ ! -d "$dir/node_modules" ]]; then
        step "Installing $label dependencies (npm install)"
        (cd "$dir" && npm install) || die "npm install failed for $label - see output above."
    fi
}

[[ "$SKIP_PRODUCT_UI" == "0" ]] && init_frontend "$FRONTEND_DIR" "src/frontend/app"
init_frontend "$ARCHITECTURE_MAP_DIR" "sandbox/frontend/architecture-map"

step 'Starting the frontend dev servers'

# architecture-map's vite.config.ts pins port 5174 with strictPort, so it's the one that actually
# fails outright if anything (a leftover process from a previous run, or the product app itself
# auto-incrementing onto it) is already listening there - checked before spawning either server.
if [[ "$SKIP_PRODUCT_UI" == "0" ]] && port_in_use 5173; then
    warn "  Port 5173 is already in use - a previous dev server for the product app may still be running. Its dev server will auto-pick the next free port instead of failing outright, but that can land it on 5174 and collide with architecture-map below."
fi

architecture_map_port_free=1
if port_in_use 5174; then
    architecture_map_port_free=0
    warn "  Port 5174 is already in use - architecture-map's dev server pins this exact port (strictPort) and will fail to start if it's taken, most likely by a previous dev server that was never stopped. Close it and re-run this script; skipping architecture-map for now."
fi

# No PowerShell-style "open a new window" equivalent that works on every Linux desktop/headless
# box - try a real terminal emulator first (so this still feels interactive on a desktop), and fall
# back to a background process logging to sandbox/logs/ otherwise (e.g. over SSH, or no GUI).
open_dev_server() {
    local dir="$1" log_name="$2"
    mkdir -p "$LOGS_DIR"
    local log_file="$LOGS_DIR/$log_name.log"
    local cmd="cd '$dir' && npm run dev"
    if [[ -n "${DISPLAY:-}" ]] && command -v gnome-terminal >/dev/null 2>&1; then
        gnome-terminal --working-directory="$dir" -- bash -c "npm run dev; exec bash" >/dev/null 2>&1 &
        disown
    elif [[ -n "${DISPLAY:-}" ]] && command -v konsole >/dev/null 2>&1; then
        konsole --workdir "$dir" -e bash -c "npm run dev; exec bash" >/dev/null 2>&1 &
        disown
    elif [[ -n "${DISPLAY:-}" ]] && command -v xterm >/dev/null 2>&1; then
        xterm -hold -e bash -c "$cmd" >/dev/null 2>&1 &
        disown
    else
        warn "  No terminal emulator found (or no \$DISPLAY) - running '$log_name' in the background, logging to $log_file"
        ( cd "$dir" && nohup npm run dev >"$log_file" 2>&1 & disown )
    fi
}

[[ "$SKIP_PRODUCT_UI" == "0" ]] && open_dev_server "$FRONTEND_DIR" "frontend-product"
[[ "$architecture_map_port_free" == "1" ]] && open_dev_server "$ARCHITECTURE_MAP_DIR" "frontend-architecture-map"

step 'Waiting for the frontend dev servers to actually come up'
product_up=0
if [[ "$SKIP_PRODUCT_UI" == "0" ]]; then
    if wait_for_dev_server "http://localhost:5173"; then
        product_up=1
        echo '  Frontend (product) is up'
    else
        warn "  Frontend (product) didn't respond within the timeout - check sandbox/logs/frontend-product.log (if it exists) or its terminal window for errors."
    fi
fi

sandbox_map_up=0
if [[ "$architecture_map_port_free" == "1" ]]; then
    if wait_for_dev_server "http://localhost:5174"; then
        sandbox_map_up=1
        echo '  Frontend (sandbox map) is up'
    else
        warn "  Frontend (sandbox map) didn't respond within the timeout - check sandbox/logs/frontend-architecture-map.log (if it exists) or its terminal window for errors."
    fi
fi

open_browser() {
    local url="$1"
    if command -v xdg-open >/dev/null 2>&1; then xdg-open "$url" >/dev/null 2>&1 &
    elif command -v open >/dev/null 2>&1; then open "$url" >/dev/null 2>&1 &
    fi
}

if [[ "$NO_BROWSER" == "0" ]]; then
    [[ "$product_up" == "1" ]] && open_browser 'http://localhost:5173'
    [[ "$sandbox_map_up" == "1" ]] && open_browser 'http://localhost:5174'
fi

printf '\nFull stack is up:\n'
if [[ "$SKIP_PRODUCT_UI" == "0" ]]; then
    suffix=""; [[ "$product_up" == "0" ]] && suffix="  (not confirmed up - see warning above)"
    echo "  Frontend (product):     http://localhost:5173$suffix"
else
    echo '  Frontend (product):     skipped (--skip-product-ui)'
fi
suffix=""; [[ "$sandbox_map_up" == "0" ]] && suffix="  (not confirmed up - see warning above)"
echo "  Frontend (sandbox map): http://localhost:5174$suffix"
echo '  AuthApi:          http://localhost:8081/scalar/v1'
echo '  LinkApi:          http://localhost:8082/scalar/v1'
echo '  RedirectApi:      http://localhost:8083/scalar/v1'
[[ "$SKIP_RABBITMQ_UI" == "0" ]] && echo '  RabbitMQ UI:      http://localhost:15672  (guest / guest)'
[[ "$SKIP_REDIS_UI" == "0" ]] && echo '  RedisInsight:     http://localhost:5540  (add a DB: host "redis-master", port 6379)'
[[ "$SKIP_POSTGRES_UI" == "0" ]] && echo '  pgweb:            http://localhost:8084'
[[ "$SKIP_MONGO_UI" == "0" ]] && echo '  Mongo Express:    http://localhost:8085'
if [[ "$SKIP_SQLITE_UI" == "0" ]]; then
    echo '  LinkApi fallback queue (sqlite-web):     http://localhost:8086'
    echo '  RedirectApi fallback queue (sqlite-web): http://localhost:8087'
fi
case "$OBSERVABILITY" in
    Full)
        echo '  Grafana:          http://localhost:3000  (Prometheus/Jaeger/Loki pre-wired)'
        echo '  Prometheus:       http://localhost:9090'
        echo '  Jaeger:           http://localhost:16686'
        echo '  Loki:             http://localhost:3100  (query via Grafana Explore, not a browsable UI)'
        echo '  Aspire Dashboard: http://localhost:18888  (up, but not receiving telemetry in Full mode)'
        ;;
    Aspire)
        echo '  Aspire Dashboard: http://localhost:18888  (logs, metrics, traces)'
        ;;
    None)
        echo '  Observability:    none - otel-collector/aspire-dashboard are not running, services drop their telemetry.'
        ;;
esac
printf '\nStop the backend with: ./stop-stack.sh\n'
echo 'Stop the frontends by closing their terminal windows (Ctrl+C), or killing the backgrounded npm process if one was started under sandbox/logs/.'
