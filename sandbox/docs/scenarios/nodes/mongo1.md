## What it is

One member of a 3-node MongoDB replica set (`rs0`) storing [TrafficService](node:traffic-service)'s
click metadata. `mongo2`/`mongo3` are symmetric peers — this doc covers all three; unlike Redis,
there's no separate Sentinel-style watcher process, the members elect among themselves.

## What it solves

See [High availability & failover](pattern:high-availability-failover).

## How it works

All 3 members run with `--replSet rs0` from the start, but a replica set doesn't exist until
something calls `rs.initiate()` once, naming the members. After that, the members themselves run
leader election (Raft-like) — no external process decides who's primary, unlike Redis's Sentinel.
`MongoDB.Driver`'s connection string lists every member and a `replicaSet=rs0` parameter; the
driver discovers and follows whichever one currently holds `PRIMARY`.

## How it's implemented here

```yaml
# sandbox/docker-compose.yml — mongo-init, a one-shot container
mongosh --host mongo1:27017 --quiet --eval '
  try {
    rs.status();
    print("replica set already initialized");
  } catch (e) {
    rs.initiate({
      _id: "rs0",
      members: [
        { _id: 0, host: "mongo1:27017" },
        { _id: 1, host: "mongo2:27017" },
        { _id: 2, host: "mongo3:27017" }
      ]
    });
  }
'
```

Idempotent by construction: `rs.status()` succeeds (and just logs "already initialized") once the
set exists, so a `docker compose up` that recreates this one-shot container harmlessly no-ops
instead of erroring or re-initiating. `TrafficService`'s connection string is
`mongo1,mongo2,mongo3/...?replicaSet=rs0` — no code change was needed when this project moved from
a single Mongo instance to this replica set; the driver already expected a connection string shaped
like this.

No auth (keyless replica set) — matches the rest of this stack's no-auth dev posture.

## Standing toggle

A "Replicas (MongoDB)" row in the architecture-map UI's Topology panel drops `mongo2`/`mongo3` out
of `rs0`'s voting configuration and stops their containers (OFF), or starts them back up and
`rs.add()`s them back in (ON) — equivalent API: `POST /api/infra/mongo-replicas {"enabled": bool}`.

Unlike Postgres/Redis, a plain container stop here is actively dangerous, not just incomplete:
`mongo1`/`mongo2`/`mongo3` were baked into `rs0`'s own voting configuration by the one-shot
`mongo-init` job's `rs.initiate()` call. Simply stopping `mongo2`/`mongo3` without first removing
them from that configuration would leave `mongo1` able to see only 1 of 3 *configured* voting
members — not a majority — so `mongo1` would step down to SECONDARY and refuse writes entirely, the
opposite of "simplify to one working instance." OFF therefore reconfigures `rs0` down to a 1-member
set *before* stopping the containers; ON starts the containers first (MongoDB gates a freshly added
member's eligibility to vote/serve reads until its initial sync completes, so there's no window of
routing to a member that isn't ready), then `rs.add()`s each one back.

Three real, live-discovered quirks this toggle has to handle:

1. **`rs.reconfig()`/`rs.add()` only succeed when run against the *current primary*.** MongoDB
   elects a primary among whichever members exist at `rs.initiate()` time with no regard for
   container naming — a fresh stack has come up with `mongo3` as primary on a real run. The toggle
   resolves the real primary by querying each node's own `rs.status()`, never assumes `mongo1`.
2. **A non-forced `rs.reconfig()` can only add or remove one voting member per call.** The OFF path
   removes `mongo2`/`mongo3` one `rs.reconfig()` at a time, re-reading `rs.conf()` fresh before each
   iteration.
3. **MongoDB refuses to let a node reconfigure itself out of the member list**, even with
   `{force: true}`. Since `mongo1` is the one permanent member, every removal has to run from
   `mongo1`'s own connection — which only works if `mongo1` is already primary. If it isn't (a
   normal occurrence per quirk 1), the toggle temporarily boosts `mongo1`'s priority to 2 and steps
   the current primary down, polling for `mongo1` to take over — see
   [this node's pitfall](pitfall:mongo-reconfig-timeout-leaves-priorities-skewed) for what happens,
   and how it's handled, when that poll times out instead of succeeding.

This whole sequence runs inside the same exclusive-toggle gate every standing toggle uses, so a
worst-case run (priority bump + stepdown + a full poll) blocks any other toggle call for up to
~15-20 seconds — acceptable for this single-operator admin tool, and non-deadlocking either way, but
worth knowing if a toggle call seems to hang briefly.

## Pitfalls

- ⚠️ [A failed "temporarily change state, then undo it" sequence can throw before the undo ever runs](pitfall:mongo-reconfig-timeout-leaves-priorities-skewed)
  (resilience-gap) — fixed

## Relatives

### Nodes

- [TrafficService](node:traffic-service)

### Patterns

- [High availability & failover](pattern:high-availability-failover)
