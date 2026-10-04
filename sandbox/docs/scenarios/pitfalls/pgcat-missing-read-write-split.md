# PgCat routed DDL and writes to a read-only replica

**Category:** architecture-bug **Status:** fixed

Watch out for the assumption that `query_parser_enabled = true` alone makes a pooler infer a
query's read/write role from its text — a separate flag is needed for that. Without it, every
query is routed by the pooler's default load-balancing, read/write role ignored: an EF Core
migration's `CREATE TABLE` or a plain `INSERT` (e.g. `AuthApi`'s `/register`) can land on a
read-only replica the same as any `SELECT`, and Postgres correctly rejects them with
`cannot execute ... in a read-only transaction` — a routing/topology mistake, not a one-off typo,
since it silently misclassifies every write until the missing flag is found.

This project's original `pgcat.toml` was based on an upstream example config that itself omits
this flag, which is easy to miss since `query_parser_enabled` *sounds* like it should be the whole
story.

⚠️ **Mistake** — `sandbox/infra/pgcat/pgcat.toml`, missing the one flag that actually does the
classification:

```toml
[pools.links_db]
pool_mode = "transaction"
query_parser_enabled = true
primary_reads_enabled = true
```

✅ **Do this instead** — the flag that actually does the classification:

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
the incident notes of the 2026-09-22 resilience pass. Confirmed
against `sandbox/infra/pgcat/pgcat.toml.example` at HEAD, which has the flag.)

## Relatives

### Nodes

- [PgCat](node:pgcat)
- [Postgres Replica 1](node:postgres-replica1)

### Patterns

- [Read-replica routing](pattern:read-replica-routing)
