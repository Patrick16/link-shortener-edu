#!/bin/sh
# Runs once during the primary's first initdb (docker-entrypoint-initdb.d scripts run after initdb
# but before the entrypoint's final server start, so a pg_hba.conf edit here is picked up from the
# very first real listen — no reload needed). The official image's generated pg_hba.conf allows
# normal client connections but not the "replication" pseudo-database, so postgres-replica1/2's
# pg_basebackup / streaming connection needs this line explicitly.
set -e

echo "host replication replicator all scram-sha-256" >> "$PGDATA/pg_hba.conf"
