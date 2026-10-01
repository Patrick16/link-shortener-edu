# No replication slot meant a disconnected replica could never catch back up

**Category:** logic-bug **Status:** fixed

The primary ran with `max_replication_slots=10` but nothing ever created or used one —
`pg_basebackup` didn't pass `-C`/`--slot`, and no `wal_keep_size` was set either. Physical
streaming replication without a slot (or a generous `wal_keep_size`) gives the primary no
guarantee it retains WAL a temporarily disconnected replica still needs.

If a replica was stopped or network-partitioned long enough that the primary recycled WAL segments
it hadn't sent yet, the replica could no longer resume streaming on reconnect — Postgres logs
"requested WAL segment ... has already been removed", and the standby stays permanently behind.
Worse, the entrypoint's idempotency check only re-ran `pg_basebackup` on an *empty* data directory,
so a replica broken this way had a non-empty `PGDATA` and would just keep retrying to stream from a
WAL position that no longer existed — it never self-healed, and needed a manual rebuild of the
whole data directory to recover.

🐛 **Bug** — `sandbox/infra/postgres/replica-entrypoint.sh`, no slot:

```bash
until pg_basebackup -h postgres -p 5432 -D "$PGDATA" -U replicator -Fp -Xs -P -R; do
  echo "Primary not ready yet, retrying base backup in 2s..."
  sleep 2
done
```

✅ **Fix** — a dedicated replication slot per replica, created by `-C` and written into the
replica's own recovery config by `-R` so every later reconnection uses it too, not just the
initial backup:

```bash
while true; do
  # A previous attempt that failed partway through can leave the slot it already created on the
  # primary, which would make -C error with "already exists" on retry - drop it first.
  psql -h postgres -p 5432 -U replicator -d postgres \
    -tAc "SELECT pg_drop_replication_slot('${REPLICA_SLOT_NAME}')" >/dev/null 2>&1 || true
  if pg_basebackup -h postgres -p 5432 -D "$PGDATA" -U replicator -Fp -Xs -P -R \
       -C --slot="${REPLICA_SLOT_NAME}"; then
    break
  fi
  echo "Primary not ready yet, retrying base backup in 2s..."
  sleep 2
done
```

A slot (over `wal_keep_size`) was the deliberate choice — it retains exactly the WAL a specific
replica still needs rather than guessing a size, and persists on the primary across restarts of
either side.

(see review-reports/2026-09-22-2106-8aa895a-postgres-redis-replication.md, F2 — fixed in `3c79e80`)

## Relatives

### Nodes

- [Postgres Replica 1](node:postgres-replica1)

### Patterns

- [High availability & failover](pattern:high-availability-failover)
