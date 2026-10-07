-- Trimmed copy of sandbox/infra/postgres/init-databases.sql for the e2e stack: just the three
-- owning databases the async flow under test actually touches. No replication role - this compose
-- file has no replicas.
CREATE DATABASE users_db;   -- owned by AuthApi
CREATE DATABASE links_db;   -- owned by ShortenerService (LinkApi/RedirectApi read the same DB)
CREATE DATABASE clicks_db;  -- owned by TrafficService
