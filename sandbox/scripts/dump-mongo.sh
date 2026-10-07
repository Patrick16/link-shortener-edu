#!/usr/bin/env bash
# Linux/macOS equivalent of dump-mongo.ps1 - see that file's header for the full rationale: dumps
# clicks_meta_db from the running `mongo1` container into a timestamped .archive.gz file under
# sandbox/backups/ (gitignored), mongodump's own binary archive format.
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
container_tmp_file="/tmp/mongo-$timestamp.archive.gz"

cd "$SANDBOX_ROOT"

container_id="$(docker compose ps -q mongo1)"
if [[ -z "$container_id" ]]; then
    echo 'Error: mongo1 container not found/running - start the stack first.' >&2
    exit 1
fi

# Full replica-set URI, not --db=clicks_meta_db against mongo1's own localhost connection -
# mongo1 isn't guaranteed to BE the primary (Mongo can fail over with zero involvement from this
# project, same as Redis Sentinel - see pitfalls/*.md), and the URI lets the driver itself find
# whichever member actually holds that role right now, same connection shape the app itself uses
# (see ConnectionStrings__Mongo in docker-compose.yml).
echo '==> Dumping clicks_meta_db from the mongo1 container'
if ! docker compose exec -T mongo1 mongodump --uri="mongodb://mongo1:27017,mongo2:27017,mongo3:27017/clicks_meta_db?replicaSet=rs0" --archive="$container_tmp_file" --gzip; then
    echo 'Error: mongodump failed - see output above.' >&2
    exit 1
fi

docker cp "$container_id:$container_tmp_file" "$out_file"
docker compose exec -T mongo1 rm -f "$container_tmp_file"

echo "Dump written to: $out_file"
echo "Restore it with: ./sandbox/scripts/restore-mongo.sh --dump-file '$out_file'"
