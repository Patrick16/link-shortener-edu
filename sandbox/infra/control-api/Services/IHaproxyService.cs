using ControlApi.Models;

namespace ControlApi.Services;

public interface IHaproxyService
{
    // Live per-backend-server status/session counts for the pgcat_back backend (pgcat-1/2/3) - not
    // polled/cached, a fresh read on every call. Null means haproxy itself couldn't be reached.
    Task<HaproxyStats?> GetHaproxyStatsAsync(CancellationToken ct);
}
