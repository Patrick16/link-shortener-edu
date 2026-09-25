namespace ControlApi.Models;

// recovery_min_apply_delay on a Postgres standby - a PGC_SIGHUP parameter, reloadable without a
// restart via ALTER SYSTEM SET + pg_reload_conf(). See DockerService.SetReplicationLagAsync.
public record ReplicationLag(int DelayMs);
