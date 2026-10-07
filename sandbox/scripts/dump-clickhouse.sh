#!/usr/bin/env bash
# Linux/macOS equivalent of dump-clickhouse.ps1 - see that file's header for the full rationale:
# dumps reports_db.clicks from the running `clickhouse` container into a timestamped .native file
# under sandbox/backups/ (gitignored), ClickHouse's own Native format.
#
# Unlike the .ps1 twin, this streams straight to the host via `>` instead of writing a
# container-local temp file and `docker cp`-ing it out - `docker compose exec -T`'s stdout is a raw
# byte stream in bash (confirmed by dump-db.sh's identical direct-redirect pattern), so the
# container-temp-file dance is only needed in PowerShell, where it works around the text pipeline
# mangling binary data.
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

cd "$SANDBOX_ROOT"

echo '==> Dumping reports_db.clicks from the clickhouse container'
if ! docker compose exec -T clickhouse clickhouse-client --query 'SELECT * FROM reports_db.clicks FORMAT Native' > "$out_file"; then
    rm -f "$out_file"
    echo 'Error: clickhouse-client export failed - see output above.' >&2
    exit 1
fi

echo "Dump written to: $out_file"
echo "Restore it with: ./sandbox/scripts/restore-clickhouse.sh --dump-file '$out_file'"
