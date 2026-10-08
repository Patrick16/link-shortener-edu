# Only the Redis master asked Sentinel who's in charge before rejoining - the replicas didn't

**Category:** architecture-bug **Status:** fixed

Watch for an HA entrypoint fix applied to only *one* role in a multi-role topology where any member
could end up playing any role. `redis-master` had a custom entrypoint
(`infra/redis/master-entrypoint.sh`) that asks Sentinel who the real master is before deciding
whether to start as master or rejoin as a replica — written specifically to stop a restarted master
from blindly coming back as an independent master after a real failover (see
[the split-brain-fencing pitfall](pitfall:redis-master-no-split-brain-fencing)). `redis-replica1`
and `redis-replica2`, meanwhile, had no equivalent check at all — their `docker-compose.yml`
`command:` hardcoded `--replicaof redis-master 6379`, unconditionally. That was fine as long as
"the master" and "the container named redis-master" stayed the same thing. But Sentinel can and
does promote a replica to master at runtime (`REPLICAOF NO ONE`) — a live, in-memory command, not a
rewrite of that replica's own `docker-compose.yml` entry. The moment a *replica* becomes the real
master and its container later restarts, it has zero memory of the promotion and just reapplies its
hardcoded command, demoting itself back to a replica of `redis-master` — which, if `redis-master`
itself correctly deferred to Sentinel in the meantime, is now trying to replicate *from* this very
node. Two nodes, each one convinced the other is in charge.

**Concrete scenario, reproduced live:** stop only `redis-master`. Sentinel correctly promotes
`redis-replica1` (quorum reached, `REPLICAOF NO ONE` issued). Now stop `redis-replica1` (the real
master) and `redis-replica2` too, so all three data nodes are down. Restart all three. `redis-master`
asks Sentinel, is told `redis-replica1` is master, and correctly rejoins as its replica. `redis-replica1`
— the actual, Sentinel-confirmed master — ignores that entirely and runs its hardcoded
`--replicaof redis-master 6379`, demoting itself. The two fight over who replicates from whom; in
the reproduction this only resolved after ~2 minutes and three separate Sentinel leader elections
(visible in its logs as repeated `+switch-master`/`+odown`/`+sdown` churn), settling back on
`redis-master` purely because of restart-timing luck, not because anything here actually decided
it correctly. A less lucky timing could leave the pair stuck fighting indefinitely, or — worse,
since this window also has two nodes each briefly believing they might be the real master —
risk diverging writes before `--min-replicas-to-write` fences either of them off.

⚠️ **Mistake** — only `redis-master` consulted Sentinel before starting; the replicas' role was a
static compose line:

```yaml
redis-replica1:
  image: redis:7-alpine
  command: ["redis-server", "--replicaof", "redis-master", "6379", "--min-replicas-to-write", "1", "--min-replicas-max-lag", "10"]
```

✅ **Do this instead** — every data node (master and both replicas) runs the *same* entrypoint,
which asks Sentinel first and only falls back to a compose-declared default role when Sentinel has
no opinion (a genuine cold start) or can't currently be reached — and even then, trusts this node's
own last Sentinel-confirmed role (persisted per-node, survives a container restart) over the static
default, so a node last confirmed as master never demotes itself just because Sentinel is
transiently unreachable:

```yaml
redis-replica1:
  image: redis:7-alpine
  volumes:
    - ./infra/redis/redis-node-entrypoint.sh:/redis-node-entrypoint.sh:ro
    - redis-replica1-state:/state
  environment:
    NODE_DEFAULT_ROLE: "replica"
    NODE_DEFAULT_REPLICAOF_HOST: "redis-master"
    NODE_DEFAULT_REPLICAOF_PORT: "6379"
  entrypoint: ["/bin/sh", "/redis-node-entrypoint.sh"]
```

Verified live: the same stop-master/let-it-fail-over/stop-everything/restart-everything sequence
above now converges on the first try, with every node agreeing with Sentinel and with each other —
no flapping, no re-elections, one single `+switch-master` event (the original real failover, not a
new one during recovery).

**The general lesson, not just this specific fix:** in a topology where any member can end up
playing any role (an HA master/replica trio, not a fixed primary/secondary pair), a "does this
container still hold the role it thinks it does" check has to run on *every* member, not just the
one that happens to be the role at design time. Writing the check once for "the master" and
assuming the replicas don't need it treats the role as a fixed property of that specific container,
when the whole point of Sentinel-driven failover is that it isn't.

## Relatives

### Nodes

- [Redis (master)](node:redis-master)
- [Redis Sentinel 1](node:redis-sentinel-1)

### Patterns

- [High availability & failover](pattern:high-availability-failover)

### Pitfalls

- [redis-master accepted writes with no fencing against a split brain](pitfall:redis-master-no-split-brain-fencing)
  — the sibling half of this same split-brain-on-rejoin problem: that one is about a demoted node
  still accepting *writes* it shouldn't, this one is about a promoted node not *knowing* it should
  stay master at all
