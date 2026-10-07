#!/usr/bin/env bash
# Linux/macOS equivalent of restore-clickhouse.ps1 - see that file's header for the full
# rationale: restores a reports_db.clicks Native-format dump (created by dump-clickhouse.sh) into
# the running `clickhouse` container, truncating the table first. Streams the dump straight from
# the host file via stdin instead of the .ps1 twin's container-temp-file + docker-cp dance -
# unnecessary in bash (see dump-clickhouse.sh).
#
# Usage:
#   ./restore-clickhouse.sh [--dump-file PATH]
#   Defaults to the newest clickhouse-*.native in sandbox/backups/ when --dump-file is omitted.

set -euo pipefail

DUMP_FILE=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --dump-file) DUMP_FILE="$2"; shift 2 ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SANDBOX_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
BACKUPS_DIR="$SANDBOX_ROOT/backups"

if [[ -z "$DUMP_FILE" ]]; then
    DUMP_FILE="$(ls -t "$BACKUPS_DIR"/clickhouse-*.native 2>/dev/null | head -n1 || true)"
    if [[ -z "$DUMP_FILE" ]]; then
        echo "Error: no dump file given and none found in $BACKUPS_DIR - run dump-clickhouse.sh first, or pass --dump-file." >&2
        exit 1
    fi
fi

if [[ ! -f "$DUMP_FILE" ]]; then
    echo "Error: dump file not found: $DUMP_FILE" >&2
    exit 1
fi

cd "$SANDBOX_ROOT"

echo "==> Restoring $DUMP_FILE into the clickhouse container"
docker compose exec -T clickhouse clickhouse-client --query 'TRUNCATE TABLE reports_db.clicks'
if ! docker compose exec -T clickhouse clickhouse-client --query 'INSERT INTO reports_db.clicks FORMAT Native' < "$DUMP_FILE"; then
    echo 'Error: clickhouse-client import failed - see output above.' >&2
    exit 1
fi

echo 'Restore complete.'
