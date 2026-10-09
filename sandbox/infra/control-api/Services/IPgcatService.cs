using ControlApi.Models;

namespace ControlApi.Services;

public interface IPgcatService
{
    // Live connection counts, read directly off ONE pgcat instance via `psql` in a Docker exec -
    // not polled/cached, a fresh snapshot on every call. Null means that container isn't running.
    // instanceId is the container/service name (pgcat-1/pgcat-2/pgcat-3, see docker-compose.yml) -
    // each instance only ever reports its own pools, there's no cross-instance aggregation here
    // (same as how postgres-replica1/2 each get their own replication-lag reading, not a merged one).
    Task<PgcatConnectionStats?> GetPgcatConnectionsAsync(string instanceId, CancellationToken ct);

    // pgcat.toml pool_mode/read-write-splitting/pool_size - rewrites the ONE file all 3 instances
    // mount read-only and relies on pgcat's own autoreload on each, no docker compose recreate
    // involved. Applies identically to all 3 instances by construction (one shared file), not a
    // per-instance setting.
    PgcatPoolSettings GetPgcatPoolSettings();

    Task<PgcatPoolSettings> SetPgcatPoolSettingsAsync(PgcatPoolSettings settings, CancellationToken ct);
}
