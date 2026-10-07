using ControlApi.Models;

namespace ControlApi.Services;

public interface IPgcatService
{
    // Live connection counts, read directly off pgcat via `psql` in a Docker exec - not
    // polled/cached, a fresh snapshot on every call. Null means the container isn't running.
    Task<PgcatConnectionStats?> GetPgcatConnectionsAsync(CancellationToken ct);

    // pgcat.toml pool_mode/read-write-splitting/pool_size - rewrites the file directly and relies
    // on pgcat's own autoreload, no docker compose recreate involved.
    PgcatPoolSettings GetPgcatPoolSettings();

    Task<PgcatPoolSettings> SetPgcatPoolSettingsAsync(PgcatPoolSettings settings, CancellationToken ct);
}
