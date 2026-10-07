#!/usr/bin/env bash
# Linux/macOS equivalent of dump-clickhouse.ps1 - see that file's header for the full rationale:
# dumps reports_db.clicks from the running `clickhouse` container into a timestamped .native file
# under sandbox/backups/ (gitignored), ClickHouse's own Native format.
#
# Usage:
#   ./dump-clickhouse.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SANDBOX_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
BACKUPS_DIR="$SANDBOX_ROOT/backups"

mkdir -p "$BACKUPS_DIR"

timestamp="$(date +%Y%m%d-%H%M%S)"
out_file="$BACKUPS_DIR/clickhouse-reports_db.clicks-$timestamp.native"
container_tmp_file="/tmp/clickhouse-clicks-$timestamp.native"

cd "$SANDBOX_ROOT"

container_id="$(docker compose ps -q clickhouse)"
if [[ -z "$container_id" ]]; then
    echo 'Error: clickhouse container not found/running - start the stack first.' >&2
    exit 1
fi

echo '==> Dumping reports_db.clicks from the clickhouse container'
if ! docker compose exec -T clickhouse sh -c "clickhouse-client --query 'SELECT * FROM reports_db.clicks FORMAT Native' > $container_tmp_file"; then
    echo 'Error: clickhouse-client export failed - see output above.' >&2
    exit 1
fi

docker cp "$container_id:$container_tmp_file" "$out_file"
docker compose exec -T clickhouse rm -f "$container_tmp_file"

echo "Dump written to: $out_file"
echo "Restore it with: ./sandbox/scripts/restore-clickhouse.sh --dump-file '$out_file'"
