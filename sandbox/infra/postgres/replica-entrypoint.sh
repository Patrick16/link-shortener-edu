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
  # -C --slot creates a permanent physical replication slot on the primary and (combined with -R)
  # writes it into this replica's recovery config as primary_slot_name, so every later streaming
  # connection uses it too, not just this one-time backup. Without a slot (or a generous
  # wal_keep_size), the primary is free to recycle WAL this replica hasn't consumed yet the moment
  # it falls behind - a replica disconnected long enough then can never resume streaming and needs a
  # manual rebuild of its whole data directory. The slot persists on the primary across restarts of
  # either side, so this only runs once per replica's lifetime (the "PGDATA is empty" guard above).
  while true; do
    # A previous attempt that failed partway through basebackup can leave the slot it already
    # created on the primary, which would make -C error with "already exists" on the next retry -
    # drop it first (ignoring failure: it may not exist yet, or the primary may not be reachable at
    # all) so every retry starts from a clean slate, the same as before -C/--slot made this
    # necessary.
    psql -h postgres -p 5432 -U replicator -d postgres -tAc "SELECT pg_drop_replication_slot('${REPLICA_SLOT_NAME}')" >/dev/null 2>&1 || true
    if pg_basebackup -h postgres -p 5432 -D "$PGDATA" -U replicator -Fp -Xs -P -R -C --slot="${REPLICA_SLOT_NAME}"; then
      break
    fi
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
