# No replication slot meant a disconnected replica could never catch back up

**Category:** infrastructure-bug **Status:** fixed

Watch for physical streaming replication set up without a replication slot (or a generous
`wal_keep_size`) — it gives the primary no guarantee it retains WAL a temporarily disconnected
replica still needs. `max_replication_slots=10` being set on the primary doesn't help if nothing
ever actually creates or uses one: `pg_basebackup` has to be told to via `-C`/`--slot`.

If a replica is stopped or network-partitioned long enough that the primary recycles WAL segments
it hadn't sent yet, the replica can no longer resume streaming on reconnect — Postgres logs
"requested WAL segment ... has already been removed", and the standby stays permanently behind.
Worse, if the entrypoint's idempotency check only re-runs `pg_basebackup` on an *empty* data
directory, a replica broken this way has a non-empty `PGDATA` and just keeps retrying to stream
from a WAL position that no longer exists — it never self-heals, and needs a manual rebuild of the
whole data directory to recover.

⚠️ **Mistake** — `sandbox/infra/postgres/replica-entrypoint.sh`, no slot:

```bash
until pg_basebackup -h postgres -p 5432 -D "$PGDATA" -U replicator -Fp -Xs -P -R; do
  echo "Primary not ready yet, retrying base backup in 2s..."
  sleep 2
done
```

✅ **Do this instead** — a dedicated replication slot per replica, created by `-C` and written into
the replica's own recovery config by `-R` so every later reconnection uses it too, not just the
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

(fixed in `3c79e80`)

## Relatives

### Nodes

- [Postgres Replica 1](node:postgres-replica1)

### Patterns

- [High availability & failover](pattern:high-availability-failover)
