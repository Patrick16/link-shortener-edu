## What it is

[PgCat](https://github.com/postgresml/pgcat) — a Postgres connection pooler and query router,
written in Rust. Scaled to 3 identical replicas (one Compose service, `deploy.replicas: 3`, shown
on the graph as one node with a `×3` badge — not 3 separate boxes) behind [HAProxy](node:haproxy),
which every .NET service actually connects to; none of them know PgCat has more than one instance,
let alone that a primary/replica Postgres topology exists at all underneath it.

## What it solves

See [Read-replica routing](pattern:read-replica-routing) for the read-scaling problem PgCat solves
at the query-routing level. PgCat is also, separately, a connection pooler in the PgBouncer sense:
each .NET service pools its own Npgsql connections, but PgCat pools the far smaller number of
*actual* Postgres connections behind that — many app-side connections share a much smaller set of
real server-side ones. Scaling PgCat itself to several replicas is a third, independent concern —
see [Pooler scaling](pattern:pooler-scaling) for why one pooler instance eventually needs to become
several, and what breaks if you just add replicas without redoing the connection-count math.

## How it works

See [Read-replica routing](pattern:read-replica-routing) for the query-classification mechanism
(`query_parser_enabled` + `query_parser_read_write_splitting` + `primary_reads_enabled`) — that part
is unaffected by how many PgCat replicas exist. See [Pooler scaling](pattern:pooler-scaling) for how
HAProxy balances across the 3 replicas and why they have to stay configuration-identical (one shared
`pgcat.toml`, not 3 independent ones).

## How it's implemented here

One pool per logical database, same primary + 2-replica trio in every pool — this part of the
config is identical across all 3 PgCat replicas, since they all mount the same `pgcat.toml`:

```toml
# sandbox/infra/pgcat/pgcat.toml.example
[pools.links_db]
pool_mode = "transaction"
query_parser_enabled = true
query_parser_read_write_splitting = true
primary_reads_enabled = true

[pools.links_db.shards.0]
servers = [["postgres", 5432, "primary"], ["postgres-replica1", 5432, "replica"], ["postgres-replica2", 5432, "replica"]]
```

`pgcat.toml` itself (not the `.example`) is gitignored — the control panel's pool-size control
rewrites it live (`PgcatService.RenderPgcatToml`), so the checked-in file would just be stale the
moment anyone touches that control; because all 3 replicas mount this one file read-only, rewriting
it once already means "change all 3 replicas," with no separate fan-out step. `ban_time = 3` (how
long a server that failed a healthcheck stays excluded) is short on purpose: a 200-VU/20-replica
load test showed a transient Docker-DNS lookup failure against a replica turning into a ~20s stall
at the old default (`ban_time = 20`). `pool_size` is **20**, not the 40 a single-instance PgCat
would use — see [Pooler scaling](pattern:pooler-scaling) for the exact connection-count math behind
that number.

**Scales with the HAProxy toggle.** This node's replica count isn't user-selectable on its own —
it's always exactly 1 or 3, driven entirely by whether [HAProxy](node:haproxy) is on or off.
Turning HAProxy off runs `docker compose up -d --scale pgcat=1 --no-recreate --no-deps pgcat`;
turning it back on scales to 3 the same way. The point isn't resource savings, it's keeping the
graph's `×N` badge honest: with HAProxy down, nothing is actually load-balancing across whatever
`pgcat` replicas exist (every DB-touching service still dials `haproxy:6432` specifically, not
pgcat's own DNS name), so the badge collapsing to `×1` reflects that truthfully instead of implying
load-balancing that isn't happening. `pgcat` stays deliberately out of the generic per-service scale
control for this reason — see [Pooler scaling](pattern:pooler-scaling) for why an arbitrary replica
count isn't safe here the way it is for a stateless API service. The same coupling also runs the
other direction: disabling pgcat itself forces HAProxy off and collapses this node back to 1
replica.

## Pitfalls

- 🐛 [PgCat routed DDL and writes to a read-only replica](pitfall:pgcat-missing-read-write-split)
  (architecture-bug) — fixed
- 🐛 [A read immediately after a write can land on a lagging replica](pitfall:pgcat-read-your-writes-race)
  (race-condition) — fixed
- ⚠️ [Rewriting a config file a process polls on a timer is not the same as that process having read it](pitfall:pgcat-toml-rewrite-not-reloaded-immediately)
  (race-condition) — fixed
- ⚠️ [A docker compose up --scale call without --no-deps silently restarts whatever it depends on](pitfall:pgcat-scale-missing-no-deps-restarts-stopped-replicas)
  (infrastructure-bug) — fixed
- ⚠️ [A control-plane process's in-memory cache of "what's currently toggled" has no source of truth once it restarts](pitfall:infra-toggle-state-resets-on-restart-desyncs-live-containers)
  (architecture-bug) — fixed
- ⚠️ [A docker-compose `${VAR}` substitution name never appears on the running container — only the real, rendered env var does](pitfall:control-api-reads-compose-substitution-names-not-rendered-env)
  (infrastructure-bug) — fixed

## Relatives

### Nodes

- [HAProxy](node:haproxy) — balances across this node's 3 replicas
- [Postgres Replica 1](node:postgres-replica1)
- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)

### Patterns

- [Read-replica routing](pattern:read-replica-routing)
- [Pooler scaling](pattern:pooler-scaling)
