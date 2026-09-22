-- Runs once, on first container start (mounted into /docker-entrypoint-initdb.d/), against the
-- default POSTGRES_DB. Creates one physical database per owning service — see the "separate
-- physical databases" note in project memory: this models microservice data isolation more
-- honestly than schemas-in-one-database would, and matches how pgcat pools get configured later
-- (scenario 4) — one pool per database name, not per schema.
CREATE DATABASE users_db;   -- owned by AuthApi
CREATE DATABASE links_db;   -- owned by ShortenerService (LinkApi/RedirectApi read the same DB)
CREATE DATABASE clicks_db;  -- owned by TrafficService

-- Streaming-replication role for postgres-replica1/2 (see init-replication-hba.sh for the matching
-- pg_hba.conf entry, and replica-entrypoint.sh for how the replicas use it). REPLICATION LOGIN, no
-- database grants needed — it connects to the special "replication" pseudo-database, not a real one.
CREATE ROLE replicator WITH REPLICATION LOGIN PASSWORD 'replicator_pass';
