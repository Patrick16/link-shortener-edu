#!/usr/bin/env bash
# Linux/macOS equivalent of dump-db.ps1 - see that file's header for the full rationale: dumps all
# Postgres databases from the running `postgres` container into a timestamped .sql file under
# sandbox/backups/ (gitignored), an on-demand snapshot on top of the named Docker volume.
#
# Usage:
#   ./dump-db.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SANDBOX_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
BACKUPS_DIR="$SANDBOX_ROOT/backups"

mkdir -p "$BACKUPS_DIR"

timestamp="$(date +%Y%m%d-%H%M%S)"
out_file="$BACKUPS_DIR/postgres-$timestamp.sql"

cd "$SANDBOX_ROOT"
echo '==> Dumping all databases from the postgres container'
if ! docker compose exec -T postgres pg_dumpall -U postgres > "$out_file"; then
    rm -f "$out_file"
    echo 'Error: pg_dumpall failed - see output above.' >&2
    exit 1
fi

echo "Dump written to: $out_file"
echo "Restore it with: ./sandbox/scripts/restore-db.sh --dump-file '$out_file'"
