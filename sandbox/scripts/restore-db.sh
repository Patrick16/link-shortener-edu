#!/usr/bin/env bash
# Linux/macOS equivalent of restore-db.ps1 - see that file's header for the full rationale:
# restores a pg_dumpall snapshot (created by dump-db.sh) into the running `postgres` container.
# The dump already contains CREATE DATABASE statements for users_db/links_db/clicks_db, so this
# connects to the `postgres` maintenance database to replay it.
#
# Usage:
#   ./restore-db.sh [--dump-file PATH]
#   Defaults to the newest file in sandbox/backups/ when --dump-file is omitted.

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
    DUMP_FILE="$(ls -t "$BACKUPS_DIR"/*.sql 2>/dev/null | head -n1 || true)"
    if [[ -z "$DUMP_FILE" ]]; then
        echo "Error: no dump file given and none found in $BACKUPS_DIR - run dump-db.sh first, or pass --dump-file." >&2
        exit 1
    fi
fi

if [[ ! -f "$DUMP_FILE" ]]; then
    echo "Error: dump file not found: $DUMP_FILE" >&2
    exit 1
fi

cd "$SANDBOX_ROOT"
echo "==> Restoring $DUMP_FILE into the postgres container"
if ! docker compose exec -T postgres psql -U postgres -d postgres < "$DUMP_FILE"; then
    echo 'Error: psql restore failed - see output above.' >&2
    exit 1
fi

echo 'Restore complete.'
