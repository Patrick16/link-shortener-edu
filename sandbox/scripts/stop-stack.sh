#!/usr/bin/env bash
# Linux/macOS equivalent of stop-stack.ps1 - see that file's header for the full rationale.
#
# Usage:
#   ./stop-stack.sh [--wipe]

set -euo pipefail

WIPE=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --wipe) WIPE=1; shift ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SANDBOX_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

cd "$SANDBOX_ROOT"

# Always activates every optional profile on `down`, regardless of which --observability mode or
# which --skip-*-ui choices start-stack.sh was run with - a profile the run never started just has
# nothing to stop, but without this a Full run's prometheus/jaeger/loki/grafana containers (or a
# prior run's pgweb/RedisInsight/Mongo Express) would be left as orphans (profiles gate `down` the
# same way they gate `up`). See docker-compose.yml for what each profile covers.
all_profile_args=(--profile observability --profile otel --profile ui-postgres --profile ui-redis --profile ui-mongo --profile ui-sqlite)

if [[ "$WIPE" == "1" ]]; then
    echo '==> Stopping backend and removing volumes (Postgres data will be wiped)'
    docker compose "${all_profile_args[@]}" down -v
else
    echo '==> Stopping backend (Postgres data kept)'
    docker compose "${all_profile_args[@]}" down
fi
