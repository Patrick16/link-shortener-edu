## What it is

Keeping a stateful service available when one of its instances dies, by running replicas and
automatically promoting one of them when the active instance disappears — without a human doing
the promotion by hand. Two independent implementations exist in this project: Redis (master + 2
replicas + 3 Sentinels watching them) and MongoDB (a 3-node replica set with built-in election).

## What it solves

[Read-replica routing](pattern:read-replica-routing) scales reads but doesn't survive the primary
dying — every write (and, for Postgres here, every DDL migration) still depends on one specific
server. HA failover is the other half: if the thing writes depend on disappears, something else
takes over automatically, fast enough that the app barely notices.

## How it works

The shape is the same regardless of which database: **N replicas of one writable primary, plus a
mechanism that (a) detects the primary is gone and (b) promotes a replica without a human in the
loop.** The two implementations here differ in *where* that mechanism lives:

- **Redis**: an external fleet (3 Sentinel processes) monitors the master, votes on whether it's
  really down (quorum, not one Sentinel's opinion), and issues the promotion
  (`REPLICAOF NO ONE` on the chosen replica). The app never talks to Sentinel for data — only to
  discover who's currently master (`serviceName=mymaster` in the connection string;
  StackExchange.Redis asks Sentinel, then connects directly to whichever node it names).
- **MongoDB**: the replica set members themselves run the election (Raft-like consensus) — no
  separate watcher process. The driver already knows the whole set's membership and follows
  whichever member currently holds `PRIMARY`.

Either way, a promoted-but-rejoining old primary is a real risk: it has no memory of having lost
its role, so without explicit fencing it can come back and accept writes as if nothing happened —
a split brain. See Pitfalls for how this bit Redis specifically here, and
`--min-replicas-to-write`/`--min-replicas-max-lag` for the mitigation (a master refuses writes once
it can't see enough replicas acknowledging them, which is also what keeps a *not-yet-detected*
split brain from silently diverging too much data before Sentinel finishes failing over).

## How it's implemented here

Redis: [Redis (master)](node:redis-master) + 2 replicas + [Redis Sentinel 1](node:redis-sentinel-1)
(+2 more). Mongo: [Mongo 1](node:mongo1) (+2 more), one-shot `rs.initiate()` on first boot only.

Neither needed app-level code changes — both drivers (`StackExchange.Redis`, `MongoDB.Driver`) take
an HA-aware connection string as-is and handle re-discovering the current primary themselves after
a failover.

## Pitfalls

- 🐛 [Replica healthchecks proved the process was up, not that replication was happening](pitfall:postgres-replica-healthcheck-doesnt-verify-streaming)
  (logic-bug) — fixed
- 🐛 [No replication slot meant a disconnected replica could never catch back up](pitfall:postgres-replica-no-replication-slot)
  (logic-bug) — fixed
- 🐛 [redis-master accepted writes with no fencing against a split brain](pitfall:redis-master-no-split-brain-fencing)
  (logic-bug) — fixed
- 🐛 [Sentinel's own hostname resolver failed even though every other tool on the same container could resolve it](pitfall:redis-sentinel-resolver-fails-despite-dns-working)
  (logic-bug) — fixed

## Relatives

### Nodes

- [Redis (master)](node:redis-master)
- [Redis Sentinel 1](node:redis-sentinel-1)
- [Mongo 1](node:mongo1)
- [Postgres Replica 1](node:postgres-replica1) — the healthcheck/replication-slot pitfalls above
  are Postgres-specific, found while building this same resilience pass

### Patterns

- [Read-replica routing](pattern:read-replica-routing)
