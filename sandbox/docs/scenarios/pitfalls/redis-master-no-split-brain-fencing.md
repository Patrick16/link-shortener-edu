# redis-master accepted writes with no fencing against a split brain

**Category:** architecture-bug **Status:** fixed

Watch for an HA failover design that handles *promoting* a new primary but never designs for
*fencing off* an old one that doesn't know it's been demoted — the two halves aren't automatically
symmetric. `redis-master` started as a plain `redis-server` with no `min-replicas-to-write`/
`min-replicas-max-lag` (or any equivalent fencing). Sentinel promotes a replica via
`REPLICAOF NO ONE` without touching server-level config on any node, and a restarted/recreated
`redis-master` always comes back up as an independent master with zero memory of a prior failover —
so a master that becomes isolated from every replica (a partition, or a recreate while Sentinels
were briefly down) keeps accepting writes instead of stepping down, producing two masters accepting
divergent writes at once: a split brain baked into the topology, not a one-off coding mistake.

**Concrete scenario:** during a partition, or a `docker compose up` recreate of `redis-master`
after Sentinel had already failed over to `redis-replica1`, the old `redis-master` kept serving
reads *and* writes as if nothing had happened. Click counters or cached entities written to it
during that window diverged silently from whatever the promoted master had, with no error surfaced
anywhere.

⚠️ **Mistake** — `sandbox/docker-compose.yml`, no fencing flags on any Redis node:

```yaml
redis-master:
  image: redis:7-alpine
  # plain redis-server, no min-replicas-* - will accept writes even fully isolated

redis-replica1:
  command: ["redis-server", "--replicaof", "redis-master", "6379"]
```

✅ **Do this instead** — every node (master and replicas alike) starts with
`--min-replicas-to-write 1 --min-replicas-max-lag 10`, so a master refuses writes the moment it
can't see at least one replica acknowledging them within an acceptable lag — closing the window
where an isolated node keeps accepting writes it shouldn't:

```yaml
redis-replica1:
  command: ["redis-server", "--replicaof", "redis-master", "6379", "--min-replicas-to-write", "1", "--min-replicas-max-lag", "10"]
```

(fixed in `834576c`)

## Relatives

### Nodes

- [Redis (master)](node:redis-master)
- [Redis Sentinel 1](node:redis-sentinel-1)

### Patterns

- [High availability & failover](pattern:high-availability-failover)

### Pitfalls

- [Only the Redis master asked Sentinel who's in charge before rejoining - the replicas didn't](pitfall:redis-replica-ignores-sentinel-on-rejoin)
  — the sibling half of this same split-brain-on-rejoin problem: this one is about a demoted node
  still accepting writes it shouldn't, that one is about a promoted node not knowing it should stay
  master at all
