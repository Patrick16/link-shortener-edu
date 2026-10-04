## What it is

Splitting database traffic between a primary (handles writes) and one or more read-only replicas
(handle reads), so read-heavy traffic doesn't all compete with writes for the same server's
capacity. Here: one Postgres primary, two streaming replicas, with [PgCat](node:pgcat) deciding
which one a given query actually goes to.

## What it solves

This project's own traffic is read-heavy by construction — a link is written once and read every
time someone clicks it. A single Postgres server has to serve both, and a write-heavy moment
(a burst of link creation) can slow down reads that have nothing to do with it, and vice versa.
Splitting reads onto dedicated replicas means read capacity scales independently of write
capacity — add more replicas, reads get faster, without touching the primary at all.

## How it works

1. The app connects to one place — PgCat — never to the primary or a replica directly.
2. PgCat **parses the query text** (`query_parser_enabled`) to classify it as a read or a write.
3. A write (or anything that isn't a plain `SELECT`) always goes to the primary.
4. A read gets load-balanced across the primary and every replica
   (`primary_reads_enabled = true` — the primary isn't excluded from read traffic, just not the
   *only* place reads can go).
5. Replicas replicate from the primary continuously via Postgres's own streaming WAL replication —
   not something PgCat is involved in at all; PgCat only decides where to *route* a query, the
   actual data replication is entirely between Postgres instances.

The trade-off this always carries: a replica is, by construction, slightly behind the primary (WAL
streaming has real but small latency). A read immediately after a write on the *same* logical
request can land on a replica that hasn't caught up yet — see Pitfalls for where this actually
bites in this project.

## How it's implemented here

```toml
# sandbox/infra/pgcat/pgcat.toml.example
[pools.links_db]
query_parser_enabled = true
query_parser_read_write_splitting = true
primary_reads_enabled = true

[pools.links_db.shards.0]
servers = [["postgres", 5432, "primary"], ["postgres-replica1", 5432, "replica"], ["postgres-replica2", 5432, "replica"]]
```

One pool per logical database (`users_db`/`links_db`/`clicks_db`), same primary+2-replicas trio in
every pool. EF Core migrations deliberately **bypass PgCat entirely**, connecting straight to the
primary via a separate `ConnectionStrings:PostgresPrimary` — DDL through a query-classifying pooler
is inherently a bit fragile (see Pitfalls), so migrations don't rely on the classifier getting it
right at all.

## Pitfalls

- 🐛 [PgCat routed DDL and writes to a read-only replica](pitfall:pgcat-missing-read-write-split)
  (architecture-bug) — fixed
- ⚠️ [A read immediately after a write can land on a lagging replica](pitfall:pgcat-read-your-writes-race)
  — known limitation

## Relatives

### Nodes

- [PgCat](node:pgcat)
- [Postgres Replica 1](node:postgres-replica1)

### Patterns

- [High availability & failover](pattern:high-availability-failover) — same "replica of a
  primary" shape, different goal (surviving a primary's death, not scaling reads) — Postgres
  replicas here do **not** fail over automatically, unlike Redis/Mongo in that pattern.
