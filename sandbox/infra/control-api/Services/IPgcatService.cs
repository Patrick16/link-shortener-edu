using ControlApi.Models;

namespace ControlApi.Services;

public interface IPgcatService
{
    // Live connection counts for every pgcat replica, read directly via `psql` in a Docker exec per
    // container - not polled/cached, a fresh snapshot on every call. pgcat is 3 identical replicas
    // behind haproxy (Pooler Scaling, deploy.replicas in docker-compose.yml), each with its own
    // independent pools - this is the per-instance breakdown, not a cross-instance aggregate.
    Task<IReadOnlyList<PgcatInstanceConnections>> GetAllPgcatConnectionsAsync(CancellationToken ct);

    // pgcat.toml pool_mode/read-write-splitting/pool_size - rewrites the ONE file all 3 instances
    // mount read-only and relies on pgcat's own autoreload on each, no docker compose recreate
    // involved. Applies identically to all 3 instances by construction (one shared file), not a
    // per-instance setting.
    PgcatPoolSettings GetPgcatPoolSettings();

    Task<PgcatPoolSettings> SetPgcatPoolSettingsAsync(PgcatPoolSettings settings, CancellationToken ct);
}
