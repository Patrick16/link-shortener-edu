# Replica healthchecks proved the process was up, not that replication was happening

**Category:** infrastructure-bug **Status:** fixed

Watch for a healthcheck that only proves the process is up, not that the thing it's actually
supposed to be doing is still happening. `pg_isready -U postgres` — identical to the primary's — is
a plausible-looking healthcheck for a replica, but it only proves the server accepts connections;
it says nothing about whether this specific server is actually a functioning standby still
receiving WAL. [PgCat](node:pgcat) and downstream services key their startup ordering off
`service_healthy` for these containers, so a healthcheck that can't tell "healthy standby" apart
from "healthy but no longer replicating" leaves a real failure mode invisible at the infra layer.

If a replica's streaming connection breaks after startup (network blip, the primary restarting, or
the replica permanently falling behind — see the companion
[replication-slot pitfall](pitfall:postgres-replica-no-replication-slot)), the Postgres *process*
keeps running and keeps answering `pg_isready` as healthy. Docker never marks it unhealthy, and
PgCat (with read/write splitting on) keeps load-balancing `SELECT`s onto it — reads through the app
silently return arbitrarily stale data, with no error, no failed healthcheck, and no visible signal
in `docker compose ps`.

⚠️ **Mistake** — `sandbox/docker-compose.yml`, both replicas:

```yaml
healthcheck:
  test: ["CMD-SHELL", "pg_isready -U postgres"]
```

✅ **Do this instead** — also check that this server is in recovery *and* has an active WAL
receiver:

```yaml
healthcheck:
  test: ["CMD-SHELL", "pg_isready -U postgres && psql -U postgres -tAc 'SELECT pg_is_in_recovery() AND EXISTS (SELECT 1 FROM pg_stat_wal_receiver)' | grep -q '^t$'"]
```

`pg_is_in_recovery()` alone isn't sufficient either — a standby stays "in recovery" even after its
streaming connection to the primary dies, it just stops receiving new WAL.
`pg_stat_wal_receiver` only has a row while the walreceiver process is actively connected, so
combining both is what actually distinguishes a healthy standby from one whose replication has
silently died.

(fixed in `3c79e80`)

## Relatives

### Nodes

- [Postgres Replica 1](node:postgres-replica1)
- [PgCat](node:pgcat)

### Patterns

- [High availability & failover](pattern:high-availability-failover)
