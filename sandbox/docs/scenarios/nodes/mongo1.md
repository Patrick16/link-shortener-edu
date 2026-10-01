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

## Pitfalls

None yet — unlike Redis's Sentinel setup, nothing broke getting this one running. Worth staying
honest about: that's not the same as "nothing can go wrong here," just that nothing has yet.

## Relatives

### Nodes

- [TrafficService](node:traffic-service)

### Patterns

- [High availability & failover](pattern:high-availability-failover)
