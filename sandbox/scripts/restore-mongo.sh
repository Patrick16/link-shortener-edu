#!/usr/bin/env bash
# Linux/macOS equivalent of restore-mongo.ps1 - see that file's header for the full rationale:
# restores a mongodump archive (created by dump-mongo.sh) into the `rs0` Mongo replica set
# (whichever of mongo1/mongo2/mongo3 is actually running), --drop replacing clicks_meta_db's
# existing collections. Streams the dump straight from the host file via stdin instead of the
# .ps1 twin's container-temp-file + docker-cp dance - unnecessary in bash (see dump-mongo.sh).
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

# See dump-mongo.sh's identical comment: picks whichever replica-set member is actually running to
# exec into, rather than assuming mongo1 specifically. mongorestore needs to WRITE (drop +
# reinsert), which only the current primary can do - the full replica-set URI below is what lets
# the driver find that primary regardless of which container runs the command.
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

echo "==> Restoring $DUMP_FILE into the mongo replica set (via $exec_service)"
if ! docker compose exec -T "$exec_service" mongorestore --uri="mongodb://mongo1:27017,mongo2:27017,mongo3:27017/clicks_meta_db?replicaSet=rs0" --archive --gzip --drop < "$DUMP_FILE"; then
    echo 'Error: mongorestore failed - see output above.' >&2
    exit 1
fi

echo 'Restore complete.'
