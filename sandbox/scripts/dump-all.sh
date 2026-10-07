#!/usr/bin/env bash
# Linux/macOS equivalent of dump-all.ps1 - runs dump-db.sh, dump-mongo.sh, and dump-clickhouse.sh
# in sequence, one command to snapshot every store this project writes to.
#
# Usage:
#   ./dump-all.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

"$SCRIPT_DIR/dump-db.sh"
"$SCRIPT_DIR/dump-mongo.sh"
"$SCRIPT_DIR/dump-clickhouse.sh"

echo '==> All dumps complete.'
