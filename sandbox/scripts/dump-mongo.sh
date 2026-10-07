#!/usr/bin/env bash
# Linux/macOS equivalent of dump-mongo.ps1 - see that file's header for the full rationale: dumps
# clicks_meta_db from the `rs0` Mongo replica set (whichever of mongo1/mongo2/mongo3 is actually
# running) into a timestamped .archive.gz file under sandbox/backups/ (gitignored), mongodump's own
# binary archive format.
#
# Unlike the .ps1 twin, this streams straight to/from the host via `>`/`<` instead of writing a
# container-local temp file and `docker cp`-ing it out - `docker compose exec -T`'s stdout/stdin is
# a raw byte stream in bash (confirmed by dump-db.sh's identical direct-redirect pattern), so the
# container-temp-file dance is only needed in PowerShell, where it works around the text pipeline
# mangling binary data.
#
# Usage:
#   ./dump-mongo.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SANDBOX_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
BACKUPS_DIR="$SANDBOX_ROOT/backups"

mkdir -p "$BACKUPS_DIR"

timestamp="$(date +%Y%m%d-%H%M%S)"
out_file="$BACKUPS_DIR/mongo-$timestamp.archive.gz"

cd "$SANDBOX_ROOT"

# Picks whichever replica-set member is actually running to exec into, rather than assuming mongo1
# specifically - mongo1 being down shouldn't block a backup when mongo2/mongo3 are healthy. The
# connection URI below (listing all three, replicaSet=rs0) is a separate concern - that's what lets
# the driver find the current PRIMARY for the actual read; this is just which container runs the
# command.
exec_service=""
for candidate in mongo1 mongo2 mongo3; do
    if [[ -n "$(docker compose ps -q "$candidate")" ]]; then
        exec_service="$candidate"
        break
    fi
done
if [[ -z "$exec_service" ]]; then
    echo 'Error: no mongo1/mongo2/mongo3 container found/running - start the stack first.' >&2
    exit 1
fi

echo "==> Dumping clicks_meta_db (via $exec_service) from the mongo replica set"
if ! docker compose exec -T "$exec_service" mongodump --uri="mongodb://mongo1:27017,mongo2:27017,mongo3:27017/clicks_meta_db?replicaSet=rs0" --archive --gzip > "$out_file"; then
    rm -f "$out_file"
    echo 'Error: mongodump failed - see output above.' >&2
    exit 1
fi

echo "Dump written to: $out_file"
echo "Restore it with: ./sandbox/scripts/restore-mongo.sh --dump-file '$out_file'"
