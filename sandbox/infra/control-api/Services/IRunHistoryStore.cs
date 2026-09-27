using ControlApi.Models;

namespace ControlApi.Services;

public interface IRunHistoryStore
{
    Task<IReadOnlyList<RunSummary>> ListAsync(CancellationToken ct);
    Task<RunSnapshot?> GetAsync(string id, CancellationToken ct);
    Task SaveAsync(RunSnapshot snapshot, CancellationToken ct);

    // Deletes one saved run by id - for picking off a handful of runs (e.g. after comparing them)
    // without wiping the whole history. Returns false if no such run exists.
    Task<bool> DeleteAsync(string id, CancellationToken ct);

    // Deletes every saved run - a manual reset for when past runs (e.g. from testing/demoing) are
    // just clutter, not something worth the usual per-run retention cap waiting them out. Returns
    // how many files it failed to delete (0 means every run was cleared) instead of throwing on the
    // first failure and leaving the rest undeleted.
    Task<int> ClearAsync(CancellationToken ct);
}
