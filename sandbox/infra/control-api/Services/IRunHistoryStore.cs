using ControlApi.Models;

namespace ControlApi.Services;

public interface IRunHistoryStore
{
    Task<IReadOnlyList<RunSummary>> ListAsync(CancellationToken ct);
    Task<RunSnapshot?> GetAsync(string id, CancellationToken ct);
    Task SaveAsync(RunSnapshot snapshot, CancellationToken ct);

    // Deletes every saved run - a manual reset for when past runs (e.g. from testing/demoing) are
    // just clutter, not something worth the usual per-run retention cap waiting them out.
    Task ClearAsync(CancellationToken ct);
}
