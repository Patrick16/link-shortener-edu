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

# PGDATA's own postgresql.conf came from pg_basebackup copying the primary's data directory - it
# holds whatever max_connections was baked into that file (the image default), NOT whatever -c flag
# the primary's own process was actually started with (CLI flags are process-only, never written to
# disk). Postgres refuses to start hot standby if a replica's max_connections is lower than the
# primary's currently-running value, so this needs its own explicit -c here, kept equal to the
# primary's via the same POSTGRES_MAX_CONNECTIONS env var (see docker-compose.yml).
exec docker-entrypoint.sh postgres -c max_connections="${POSTGRES_MAX_CONNECTIONS:-100}"
