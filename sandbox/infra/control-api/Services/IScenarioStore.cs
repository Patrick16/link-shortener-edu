using ControlApi.Models;

namespace ControlApi.Services;

public interface IScenarioStore
{
    Task<IReadOnlyList<CustomScenario>> ListAsync(CancellationToken ct);
    Task SaveAsync(CustomScenario scenario, CancellationToken ct);
    Task<bool> DeleteAsync(string name, CancellationToken ct);
}
