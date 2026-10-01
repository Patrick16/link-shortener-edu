# redis-master accepted writes with no fencing against a split brain

**Category:** logic-bug **Status:** fixed

`redis-master` started as a plain `redis-server` with no `min-replicas-to-write`/
`min-replicas-max-lag` (or any equivalent fencing). Sentinel promotes a replica via
`REPLICAOF NO ONE` without touching server-level config on any node, and a restarted/recreated
`redis-master` always came back up as an independent master with zero memory of a prior failover —
so a master that became isolated from every replica (a partition, or a recreate while Sentinels
were briefly down) kept accepting writes instead of stepping down, producing two masters accepting
divergent writes at once.

**Concrete scenario:** during a partition, or a `docker compose up` recreate of `redis-master`
after Sentinel had already failed over to `redis-replica1`, the old `redis-master` kept serving
reads *and* writes as if nothing had happened. Click counters or cached entities written to it
during that window diverged silently from whatever the promoted master had, with no error surfaced
anywhere.

🐛 **Bug** — `sandbox/docker-compose.yml`, no fencing flags on any Redis node:

```yaml
redis-master:
  image: redis:7-alpine
  # plain redis-server, no min-replicas-* - will accept writes even fully isolated

redis-replica1:
  command: ["redis-server", "--replicaof", "redis-master", "6379"]
```

✅ **Fix** — every node (master and replicas alike) starts with
`--min-replicas-to-write 1 --min-replicas-max-lag 10`, so a master refuses writes the moment it
can't see at least one replica acknowledging them within an acceptable lag — closing the window
where an isolated node keeps accepting writes it shouldn't:

```yaml
redis-replica1:
  command: ["redis-server", "--replicaof", "redis-master", "6379", "--min-replicas-to-write", "1", "--min-replicas-max-lag", "10"]
```

(see review-reports/2026-09-22-2106-8aa895a-postgres-redis-replication.md, F3 — fixed in `834576c`)

## Relatives

### Nodes

- [Redis (master)](node:redis-master)
- [Redis Sentinel 1](node:redis-sentinel-1)

### Patterns

- [High availability & failover](pattern:high-availability-failover)
