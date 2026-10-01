## What it is

[PgCat](https://github.com/postgresml/pgcat) — a Postgres connection pooler and query router,
written in Rust. The only thing any of the five .NET services connect to for Postgres; none of
them know the primary/replica topology exists.

## What it solves

See [Read-replica routing](pattern:read-replica-routing) for the read-scaling problem. PgCat is
also, separately, a connection pooler in the PgBouncer sense: each .NET service pools its own
Npgsql connections, but PgCat pools the far smaller number of *actual* Postgres connections behind
that — many app-side connections share a much smaller set of real server-side ones.

## How it works

See [Read-replica routing](pattern:read-replica-routing) for the query-classification mechanism
(`query_parser_enabled` + `query_parser_read_write_splitting` + `primary_reads_enabled`).

## How it's implemented here

One pool per logical database, same primary + 2-replica trio in every pool:

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
rewrites it live (`DockerService.RenderPgcatToml`), so the checked-in file would just be stale the
moment anyone touches that control. `ban_time = 3` (how long a server that failed a healthcheck
stays excluded) is short on purpose: a 200-VU/20-replica load test showed a transient Docker-DNS
lookup failure against a replica turning into a ~20s stall at the old default (`ban_time = 20`).

## Pitfalls

- 🐛 [PgCat routed DDL and writes to a read-only replica](pitfall:pgcat-missing-read-write-split)
  (logic-bug) — fixed
- ⚠️ [A read immediately after a write can land on a lagging replica](pitfall:pgcat-read-your-writes-race)
  — known limitation

## Relatives

### Nodes

- [Postgres Replica 1](node:postgres-replica1)
- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)

### Patterns

- [Read-replica routing](pattern:read-replica-routing)
