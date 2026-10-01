# Replica healthchecks proved the process was up, not that replication was happening

**Category:** logic-bug **Status:** fixed

Both replicas' healthcheck was `pg_isready -U postgres` — identical to the primary's. `pg_isready`
only proves the server accepts connections; it says nothing about whether this specific server is
actually a functioning standby still receiving WAL. [PgCat](node:pgcat) and downstream services key
their startup ordering off `service_healthy` for these containers.

If a replica's streaming connection broke after startup (network blip, the primary restarting, or
the replica permanently falling behind — see the companion
[replication-slot pitfall](pitfall:postgres-replica-no-replication-slot)), the Postgres *process*
kept running and answering `pg_isready` as healthy. Docker never marked it unhealthy, and PgCat
(with read/write splitting on) kept load-balancing `SELECT`s onto it — reads through the app
silently returned arbitrarily stale data, with no error, no failed healthcheck, and no visible
signal in `docker compose ps`.

🐛 **Bug** — `sandbox/docker-compose.yml`, both replicas:

```yaml
healthcheck:
  test: ["CMD-SHELL", "pg_isready -U postgres"]
```

✅ **Fix** — also checks that this server is in recovery *and* has an active WAL receiver:

```yaml
healthcheck:
  test: ["CMD-SHELL", "pg_isready -U postgres && psql -U postgres -tAc 'SELECT pg_is_in_recovery() AND EXISTS (SELECT 1 FROM pg_stat_wal_receiver)' | grep -q '^t$'"]
```

`pg_is_in_recovery()` alone isn't sufficient either — a standby stays "in recovery" even after its
streaming connection to the primary dies, it just stops receiving new WAL.
`pg_stat_wal_receiver` only has a row while the walreceiver process is actively connected, so
combining both is what actually distinguishes a healthy standby from one whose replication has
silently died.

(see review-reports/2026-09-22-2106-8aa895a-postgres-redis-replication.md, F1 — fixed in `3c79e80`)

## Relatives

### Nodes

- [Postgres Replica 1](node:postgres-replica1)
- [PgCat](node:pgcat)

### Patterns

- [High availability & failover](pattern:high-availability-failover)
