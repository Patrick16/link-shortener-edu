#!/bin/sh
# Custom entrypoint for postgres-replica1/2 — the official postgres image has no built-in "start as
# a streaming replica" mode, so this does the two-step dance by hand: take a base backup from the
# primary into an empty PGDATA (only on first start — a populated PGDATA means this replica already
# has a base backup and its own replay history, re-running pg_basebackup would destroy that), then
# hand off to the normal entrypoint to start postgres, which sees standby.signal (written by -R
# below) and comes up in hot-standby/streaming mode instead of as a primary.
set -e

# -R alone doesn't reliably persist the password for the *ongoing* replication stream across
# restarts (depends on how the connection was authenticated) — a .pgpass file makes every libpq
# connection this user makes, base backup and streaming alike, password-resolvable independent of
# that.
echo "postgres:5432:*:replicator:replicator_pass" > ~/.pgpass
chmod 600 ~/.pgpass

if [ -z "$(ls -A "$PGDATA" 2>/dev/null)" ]; then
  echo "PGDATA is empty — taking a fresh base backup from the primary..."
  until pg_basebackup -h postgres -p 5432 -D "$PGDATA" -U replicator -Fp -Xs -P -R; do
    echo "Primary not ready yet, retrying base backup in 2s..."
    sleep 2
  done
fi

exec docker-entrypoint.sh postgres
