-- Runs once, on first container start (mounted into /docker-entrypoint-initdb.d/, the same
-- mechanism Postgres's own init-databases.sql uses) - creates the one table ReportingService
-- writes and ReportingApi reads. No app code ever creates or alters this schema; there's no EF
-- Core provider for ClickHouse, so unlike Postgres there's no migration step to run instead.
--
-- ReplacingMergeTree, not plain MergeTree: a redelivered ClickTrackedEvent (RabbitMQ requeue,
-- or a later replay) inserts another row with the same (hash, id) - ReplacingMergeTree collapses
-- those to one row during a background merge, so this consumer never needs to query ClickHouse
-- first to check what's already there (an expensive point-lookup pattern ClickHouse is
-- specifically bad at). The collapse isn't immediate - a query right after a redelivery can still
-- see a duplicate until the next merge runs (or a query adds FINAL, at a real performance cost) -
-- that's intentional here, not a bug: it's the same "eventually consistent, not instant" trade-off
-- already documented elsewhere in this project, just showing up in a new place.
--
-- ORDER BY (hash, id): hash leads because every read query filters by it (GET /reports/{hash}/
-- summary); id is included so the dedup key only ever collapses genuine redeliveries of the same
-- click, never two different clicks that happen to land in the same hash+day.
--
-- Own database (reports_db), not the server's default one - same "one database per owning
-- service" convention Postgres's init-databases.sql already follows for users_db/links_db/clicks_db.
CREATE DATABASE IF NOT EXISTS reports_db;

-- TTL (2026-10-06): rows are deleted once they're older than the retention window - enforced lazily,
-- during ClickHouse's own background merges (same mechanism that collapses ReplacingMergeTree
-- duplicates above), not on a separate schedule. 90 days matches MongoClickMetaStore's own
-- RetentionWindow (src/backend/Shared/Infrastructure/MongoClickMetaStore.cs) so the retention story
-- is consistent across every store this project writes click data to - this table already
-- partitions by day for dedup, but partitioning alone never deleted anything on its own; nothing in
-- this project actually expired old data anywhere until this and the Mongo TTL index were added.
CREATE TABLE IF NOT EXISTS reports_db.clicks
(
    hash String,
    id UUID,
    clicked_at DateTime,
    country String,
    device_type String,
    browser String,
    os String,
    referrer_domain String
)
ENGINE = ReplacingMergeTree
PARTITION BY toYYYYMMDD(clicked_at)
ORDER BY (hash, id)
TTL clicked_at + INTERVAL 90 DAY DELETE;
