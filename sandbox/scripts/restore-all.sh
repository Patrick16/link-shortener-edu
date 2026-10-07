#!/usr/bin/env bash
# Linux/macOS equivalent of restore-all.ps1 - runs restore-db.sh, restore-mongo.sh, and
# restore-clickhouse.sh in sequence, each against the newest dump of its own kind in
# sandbox/backups/. Pass explicit per-store file paths if you need anything other than "the
# latest of each" (call the three scripts directly instead).
#
# Usage:
#   ./restore-all.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

"$SCRIPT_DIR/restore-db.sh"
"$SCRIPT_DIR/restore-mongo.sh"
"$SCRIPT_DIR/restore-clickhouse.sh"

echo '==> All restores complete.'
