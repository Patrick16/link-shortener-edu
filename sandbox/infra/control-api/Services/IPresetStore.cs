using ControlApi.Models;

namespace ControlApi.Services;

public interface IPresetStore
{
    Task<IReadOnlyList<Preset>> ListAsync(CancellationToken ct);
    Task SaveAsync(Preset preset, CancellationToken ct);
    Task<bool> DeleteAsync(string name, CancellationToken ct);
}
