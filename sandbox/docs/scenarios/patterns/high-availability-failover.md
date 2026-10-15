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

**Live demo controls for this pattern.** Both implementations now have a standing toggle in the
architecture-map UI's Topology panel that stops/starts their real HA containers and re-renders the
graph to match — previously the only way to see either story degrade was chaos-killing a container
by hand and watching the system cope, with no clean "turn HA off, see a plain single node, turn it
back on" path. Redis: a "Redis" toggle (the node itself) with a nested "Redis Sentinel (HA)" toggle
underneath — see [Redis (master)](node:redis-master) and
[Redis Sentinel 1](node:redis-sentinel-1)'s own "Standing toggle" sections. MongoDB: a "Replicas
(MongoDB)" toggle — see [Mongo 1](node:mongo1)'s own "Standing toggle" section, including the
voting-configuration quirk that makes Mongo's version of this control structurally harder than
Redis's or Postgres's (a plain container stop here would leave the one remaining member unable to
reach a voting majority, unlike either other store).

## Pitfalls

- 🐛 [Replica healthchecks proved the process was up, not that replication was happening](pitfall:postgres-replica-healthcheck-doesnt-verify-streaming)
  (infrastructure-bug) — fixed
- 🐛 [No replication slot meant a disconnected replica could never catch back up](pitfall:postgres-replica-no-replication-slot)
  (infrastructure-bug) — fixed
- 🐛 [redis-master accepted writes with no fencing against a split brain](pitfall:redis-master-no-split-brain-fencing)
  (architecture-bug) — fixed
- 🐛 [Sentinel's own hostname resolver failed even though every other tool on the same container could resolve it](pitfall:redis-sentinel-resolver-fails-despite-dns-working)
  (infrastructure-bug) — fixed
- 🐛 [Only the Redis master asked Sentinel who's in charge before rejoining - the replicas didn't](pitfall:redis-replica-ignores-sentinel-on-rejoin)
  (architecture-bug) — fixed
- ⚠️ [A CLI flag baked in on every container start can quietly undo a runtime CONFIG SET](pitfall:redis-min-replicas-to-write-not-resynced-on-restart)
  (resilience-gap) — fixed
- ⚠️ [A failed "temporarily change state, then undo it" sequence can throw before the undo ever runs](pitfall:mongo-reconfig-timeout-leaves-priorities-skewed)
  (resilience-gap) — fixed

## Relatives

### Nodes

- [Redis (master)](node:redis-master)
- [Redis Sentinel 1](node:redis-sentinel-1)
- [Mongo 1](node:mongo1)
- [Postgres Replica 1](node:postgres-replica1) — the healthcheck/replication-slot pitfalls above
  are Postgres-specific, found while building this same resilience pass

### Patterns

- [Read-replica routing](pattern:read-replica-routing)
