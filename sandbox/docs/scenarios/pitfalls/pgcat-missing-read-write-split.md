# PgCat routed DDL and writes to a read-only replica

**Category:** logic-bug **Status:** fixed

`query_parser_enabled = true` alone does **not** make PgCat infer a query's read/write role from
its text — a separate flag is needed for that. Without it, an EF Core migration's `CREATE TABLE`
and a plain `INSERT` (e.g. `AuthApi`'s `/register`) could both get load-balanced onto a read-only
replica the same as any `SELECT`, and Postgres correctly rejected them:
`cannot execute ... in a read-only transaction`. This crashed `TrafficService` on startup (its
migration failed) and 500'd `AuthApi`'s `/register` — found live, not by reading the config.

This project's original `pgcat.toml` was based on an upstream example config that itself omits
this flag, which is easy to miss since `query_parser_enabled` *sounds* like it should be the whole
story.

🐛 **Bug** — `sandbox/infra/pgcat/pgcat.toml`, missing the one flag that actually does the
classification:

```toml
[pools.links_db]
pool_mode = "transaction"
query_parser_enabled = true
primary_reads_enabled = true
```

✅ **Fix** — the flag that was actually missing:

```toml
[pools.links_db]
pool_mode = "transaction"
query_parser_enabled = true
query_parser_read_write_splitting = true
primary_reads_enabled = true
```

EF Core migrations additionally now bypass PgCat entirely, connecting straight to the primary via
a dedicated `ConnectionStrings:PostgresPrimary` — belt and suspenders, since DDL through any
query-classifying pooler is inherently a bit fragile regardless of this specific flag.

(fixed live before being committed — no separate buggy commit exists to diff; reconstructed from
`.notes/PLAN.md`'s description of the incident, the 2026-09-22 "resilience pass" entry. Confirmed
against `sandbox/infra/pgcat/pgcat.toml.example` at HEAD, which has the flag.)

## Relatives

### Nodes

- [PgCat](node:pgcat)
- [Postgres Replica 1](node:postgres-replica1)

### Patterns

- [Read-replica routing](pattern:read-replica-routing)
