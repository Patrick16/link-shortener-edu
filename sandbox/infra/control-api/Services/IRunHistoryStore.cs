using ControlApi.Models;

namespace ControlApi.Services;

public interface IRunHistoryStore
{
    Task<IReadOnlyList<RunSummary>> ListAsync(CancellationToken ct);
    Task<RunSnapshot?> GetAsync(string id, CancellationToken ct);
    Task SaveAsync(RunSnapshot snapshot, CancellationToken ct);
}
