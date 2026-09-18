-- Runs once, on first container start (mounted into /docker-entrypoint-initdb.d/), against the
-- default POSTGRES_DB. Creates one physical database per owning service — see the "separate
-- physical databases" note in project memory: this models microservice data isolation more
-- honestly than schemas-in-one-database would, and matches how pgcat pools get configured later
-- (scenario 4) — one pool per database name, not per schema.
CREATE DATABASE users_db;   -- owned by AuthApi
CREATE DATABASE links_db;   -- owned by ShortenerService (LinkApi/RedirectApi read the same DB)
CREATE DATABASE clicks_db;  -- owned by TrafficService
