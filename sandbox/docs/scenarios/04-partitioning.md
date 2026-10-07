# Scenario 4 — Postgres Partitioning (Data Retention)

> **Status:** backend and infra done and verified live 2026-10-06, including against the real
> stack's existing data (not just a fresh database). The partition-maintenance job (creating future
> partitions ahead of time, dropping ones past the retention window) is **not built** — see "What's
> still missing" below. No frontend piece was built either (decided not worth it — see that section
> too).

**This scenario replaced "+ PgCat with 2 Shards (Write Scaling)"** on 2026-10-06, after reviewing
an external gap analysis that pointed out `clicks_db` (Postgres) and `clicks_meta_db` (Mongo) both
grow unbounded with no retention story anywhere in this project — not even ClickHouse's
`reports_db.clicks`, which partitioned by day for dedup but had no `TTL` clause, so nothing
anywhere ever actually deleted old data. Write-sharding was judged less valuable to demonstrate
here than data lifecycle management, a distinct pattern this stand didn't cover at all — it's
dropped, not deferred; see `.notes/PLAN.md`'s Mental Model section for the full reasoning.

## What this demonstrates

Three different stores, three different retention mechanisms, on purpose:

- **Postgres (`clicks_db.clicks`)** — native declarative partitioning
  (`PARTITION BY RANGE ("ClickedAt")`), monthly. Retention here means `DROP`/`DETACH PARTITION`
  dropping a whole partition at once, instead of a `DELETE ... WHERE` that has to find and remove
  matching rows one at a time. Partitioning also narrows which partition(s) a time-bounded query
  even has to look at (see "Partition pruning" below) — a retention mechanism and a query-performance
  mechanism, for free, from the same feature.
- **Mongo (`clicks_meta_db.clicks`)** — a TTL index on `ClickedAt`. Mongo's background TTL monitor
  (runs roughly once a minute) deletes expired documents on its own — no partitions, no job to build
  or schedule, the database does it.
- **ClickHouse (`reports_db.clicks`)** — a `TTL` clause on the table itself
  (`clicked_at + INTERVAL 90 DAY DELETE`). Enforced lazily, during ClickHouse's own background
  merges — the same mechanism that already collapsed `ReplacingMergeTree` duplicates here (see
  `sandbox/infra/clickhouse/init.sql`'s own comment) now also drops expired rows during the same
  pass.

Mongo and ClickHouse both use a **90-day** retention window, deliberately kept in step with each
other (`MongoClickMetaStore.RetentionWindow`, `clicked_at + INTERVAL 90 DAY DELETE`) so the story
is the same number in two different stores, not two unrelated numbers. Postgres's partitions aren't
on an enforced retention window at all right now — see "What's still missing".

## Converting an existing table to partitioned

Postgres has no `ALTER TABLE ... PARTITION BY` — you cannot partition a table in place. The real
technique, used in `TrafficService.Migrations.ConvertClicksToPartitionedTable`:

1. Rename the old table (and its primary-key constraint — renaming a table does **not** rename its
   constraints/indexes, so the old constraint's name has to be freed explicitly or the new table's
   identically-named one collides with it).
2. Create a new table, `PARTITION BY RANGE ("ClickedAt")`, with the composite primary key Postgres
   requires (`("Id", "ClickedAt")` — the partition key must be part of every unique/primary key on a
   partitioned table).
3. Create the initial partitions.
4. Copy every row from the old table into the new one.
5. Drop the old table.

`Click`'s primary key changing from `Id` alone to `("Id", "ClickedAt")` needed no other code
changes — `ClickTrackedConsumer`'s redelivery check is a plain `WHERE "Id" = ANY(...)`, not
`Find`/`Attach`, and `Id` leads the composite index, so it's still used efficiently for an
`Id`-only lookup.

**Bootstrap partitions are computed at migration-*run*-time, not hardcoded.** The migration's SQL
uses `now()` inside a `DO $$ ... $$` block to create 6 monthly partitions — 2 months back through 3
months ahead of whenever `dotnet ef database update` actually executes — plus one `DEFAULT`
partition that catches anything outside that window (older historical data, or any insert past the
bootstrap range if nobody's created a partition for it yet). Hardcoding literal dates into the
migration would have meant the bootstrap window drifting out of relevance the moment enough time
passed between writing the migration and actually running it against a fresh database.

**Verified against real data, not an empty database:** this migration ran against the live stack's
`clicks_db` with 3,470,328 real rows already in it (from this project's own load-testing history),
backed up first (`pg_dump -t clicks`). Row count was identical afterward; every row landed in the
correct monthly partition (verified by querying each partition's own count directly), zero rows
fell into `clicks_default`.

## Partition pruning

```bash
docker exec -it $(docker compose ps -q postgres) psql -U postgres -d clicks_db -c "
EXPLAIN SELECT count(*) FROM clicks WHERE \"ClickedAt\" >= '2026-10-01' AND \"ClickedAt\" < '2026-11-01';
"
```

The query plan touches only the one partition the date range actually falls in (e.g.
`clicks_y2026m10`) — every other partition is skipped entirely, not even considered. A query
without a `WHERE` on `ClickedAt` (or one spanning several months) scans every partition it needs to,
same as an unpartitioned table would have for the whole thing.

## Try it yourself

```bash
# List every partition and its bounds
docker exec -it $(docker compose ps -q postgres) psql -U postgres -d clicks_db -c '\d+ clicks'

# Row count per partition
docker exec -it $(docker compose ps -q postgres) psql -U postgres -d clicks_db -c "
SELECT tableoid::regclass AS partition, count(*) FROM clicks GROUP BY partition ORDER BY partition;
"

# Mongo's TTL index
docker exec -it $(docker compose ps -q mongo1) mongosh --quiet clicks_meta_db --eval "db.clicks.getIndexes()"
# -> expireAfterSeconds: 7776000 (90 days)

# ClickHouse's TTL clause
docker exec -it $(docker compose ps -q clickhouse) clickhouse-client --query "SHOW CREATE TABLE reports_db.clicks"
# -> ... TTL clicked_at + toIntervalDay(90)
```

## What's still missing

- **Partition-maintenance job** — nothing creates next month's partition ahead of time, or drops
  partitions past a retention window, for Postgres. **Decided 2026-10-06 not to build one right
  now**, as an explicit, documented gap rather than a silent omission: the 6-month bootstrap window
  plus the `clicks_default` safety net means inserts don't start *failing* without it — new data
  just falls into the slower `DEFAULT` partition once the bootstrap window is exhausted, and old
  partitions simply accumulate instead of ever being dropped. **This is not a free "nothing
  breaks," though** — once real data has accumulated in `clicks_default`, fixing it by creating a
  proper dated partition requires `ALTER TABLE clicks ATTACH PARTITION ... FOR VALUES FROM (...) TO
  (...)`, and Postgres has to scan the entire `DEFAULT` partition under a heavy lock first, to prove
  no row in it violates the new partition's bounds, before the attach can succeed. At the
  3.47M-row scale this project already tested, that scan+lock is a real, possibly multi-minute
  outage window on the live table — not something to attempt casually once `DEFAULT` has grown.
  Real options for building the maintenance job that avoids ever reaching that state, none chosen
  yet: a small scheduled worker (same shape as this project's existing `*Service` workers), the
  `pg_partman` extension (Postgres-native, declarative, but a new extension dependency), or a manual
  script in `sandbox/scripts/` (same spirit as `dump-db.ps1`/`.sh`).
- **`Click`'s composite primary key no longer enforces `Id` as globally unique on its own** — only
  the full `(Id, ClickedAt)` pair, since Postgres has no way to express a true cross-partition
  unique constraint on a non-partition-key column. `clicks_meta_db.ClickMeta` (Mongo) is upserted by
  `Id` alone, so a hypothetical row pair sharing an `Id` with different `ClickedAt` values would
  desync the two stores — nothing in this codebase currently produces that pairing (`Id` and
  `ClickedAt` are generated once together and travel through every redelivery unchanged), so this is
  a structural limitation to stay aware of, not an active bug. See `DatabaseContext.cs`'s own
  comment on this for the same note closer to the code.
- **No enforced retention window for Postgres** — Mongo and ClickHouse both actually delete data
  past 90 days; Postgres's partitions just exist, nothing drops old ones (the maintenance job above
  would be what actually enforces a window there).
- **Frontend** — a partition-count/oldest-retained-data panel on the architecture-map was considered
  and **decided against** (2026-10-06): the operational reality here is directly inspectable via
  `psql`/`EXPLAIN` (see above), and a live panel didn't seem worth the added UI surface for what
  this scenario is actually teaching.

## Where the code lives

| What | Path |
|---|---|
| Partitioning migration | `src/backend/Services/TrafficService/Migrations/20261006173517_ConvertClicksToPartitionedTable.cs` |
| Composite PK model change | `src/backend/Services/TrafficService/DatabaseContext.cs` |
| `Click` record | `src/backend/Shared/Common/Models/Click.cs` |
| Mongo TTL index | `src/backend/Shared/Infrastructure/MongoClickMetaStore.cs` (`EnsureIndexesAsync`) |
| TTL index applied at startup | `src/backend/Services/TrafficService/Program.cs` |
| ClickHouse TTL clause | `sandbox/infra/clickhouse/init.sql` |
