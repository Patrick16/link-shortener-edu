## What it is

One of 3 [Redis](https://redis.io/docs/latest/operate/oss_and_stack/management/sentinel/)
Sentinel processes that monitor [Redis (master)](node:redis-master) and its replicas and vote on
failover. `redis-sentinel-2`/`-3` are symmetric peers — this doc covers all three; the point of
running 3 is quorum (tolerating 1-2 being unreachable without false-triggering a failover).

## What it solves

See [High availability & failover](pattern:high-availability-failover).

## How it works

Sentinel is a separate process from the Redis instances it watches — it never serves application
data itself. It (1) monitors master and replica health, (2) agrees with the other Sentinels
(quorum) on whether the master is really down, not just briefly slow, and (3) issues the promotion
(`REPLICAOF NO ONE` on the chosen replica) when quorum agrees.

The app never queries Sentinel for data — `StackExchange.Redis`'s Sentinel-aware client asks it
"who is `mymaster` right now" and connects directly to whichever node it names, re-asking
automatically after a connection failure.

## How it's implemented here

```
# ConnectionStrings__Redis in docker-compose.yml
redis-sentinel-1:26379,redis-sentinel-2:26379,redis-sentinel-3:26379,serviceName=mymaster
```

The client talks to all 3 Sentinel addresses, not one — any single Sentinel being briefly down
doesn't affect the app's ability to find the current master. Sentinel's own config
(`sentinel monitor mymaster <ip> 6379 2`, quorum 2 of 3) is generated at container start, not
bind-mounted read-only, because Sentinel rewrites its own config file on every state change (a
failover, a newly-discovered replica) — see this node's Pitfalls for why that generation step
turned out to be non-trivial.

## Pitfalls

- 🐛 [Sentinel's own hostname resolver failed even though every other tool on the same container could resolve it](pitfall:redis-sentinel-resolver-fails-despite-dns-working)
  (infrastructure-bug) — fixed

## Relatives

### Nodes

- [Redis (master)](node:redis-master)

### Patterns

- [High availability & failover](pattern:high-availability-failover)
