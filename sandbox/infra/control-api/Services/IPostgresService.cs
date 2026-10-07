using ControlApi.Models;

namespace ControlApi.Services;

public interface IPostgresService
{
    // Live connection counts, read directly off postgres via `psql` in a Docker exec - not
    // polled/cached, a fresh snapshot on every call. Null means the container isn't running.
    Task<PostgresConnectionStats?> GetPostgresConnectionsAsync(CancellationToken ct);

    // Artificial WAL-replay delay on one Postgres standby (postgres-replica1/postgres-replica2) via
    // recovery_min_apply_delay - live SQL against the target container, no file edit or restart.
    // Null means the given serviceId isn't a known replica.
    Task<ReplicationLag?> GetReplicationLagAsync(string serviceId, CancellationToken ct);

    Task<ReplicationLag?> SetReplicationLagAsync(string serviceId, int delayMs, CancellationToken ct);
}
