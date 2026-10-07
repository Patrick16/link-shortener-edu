#!/usr/bin/env bash
# Linux/macOS equivalent of restore-mongo.ps1 - see that file's header for the full rationale:
# restores a mongodump archive (created by dump-mongo.sh) into the running `mongo1` container,
# --drop replacing clicks_meta_db's existing collections.
#
# Usage:
#   ./restore-mongo.sh [--dump-file PATH]
#   Defaults to the newest mongo-*.archive.gz in sandbox/backups/ when --dump-file is omitted.

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
    DUMP_FILE="$(ls -t "$BACKUPS_DIR"/mongo-*.archive.gz 2>/dev/null | head -n1 || true)"
    if [[ -z "$DUMP_FILE" ]]; then
        echo "Error: no dump file given and none found in $BACKUPS_DIR - run dump-mongo.sh first, or pass --dump-file." >&2
        exit 1
    fi
fi

if [[ ! -f "$DUMP_FILE" ]]; then
    echo "Error: dump file not found: $DUMP_FILE" >&2
    exit 1
fi

cd "$SANDBOX_ROOT"

container_id="$(docker compose ps -q mongo1)"
if [[ -z "$container_id" ]]; then
    echo 'Error: mongo1 container not found/running - start the stack first.' >&2
    exit 1
fi
container_tmp_file="/tmp/mongo-restore.archive.gz"

# Full replica-set URI - mongorestore needs to WRITE (drop + reinsert), which only the current
# primary can do, and mongo1 isn't guaranteed to be it (see dump-mongo.sh's identical comment).
echo "==> Restoring $DUMP_FILE into the mongo1 container"
docker cp "$DUMP_FILE" "$container_id:$container_tmp_file"
if ! docker compose exec -T mongo1 mongorestore --uri="mongodb://mongo1:27017,mongo2:27017,mongo3:27017/clicks_meta_db?replicaSet=rs0" --archive="$container_tmp_file" --gzip --drop; then
    echo 'Error: mongorestore failed - see output above.' >&2
    exit 1
fi
docker compose exec -T mongo1 rm -f "$container_tmp_file"

echo 'Restore complete.'
