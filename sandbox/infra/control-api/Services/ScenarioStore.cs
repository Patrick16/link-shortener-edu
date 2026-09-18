using System.Text.Json;
using ControlApi.Models;

namespace ControlApi.Services;

// A flat JSON file, not a database - this is a handful of hand-drawn load profiles for one local
// user, not shared multi-writer state. The file lives under Scenarios:DataDir, which the compose
// file points at a named volume (not the read-only ./:/workspace:ro mount control-api also has) so
// saved scenarios survive a control-api rebuild the same way postgres-data survives one.
public class ScenarioStore : IScenarioStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public ScenarioStore(IConfiguration configuration)
    {
        var dataDir = configuration["Scenarios:DataDir"] ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "custom-scenarios.json");
    }

    public async Task<IReadOnlyList<CustomScenario>> ListAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return await ReadAllAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(CustomScenario scenario, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = (await ReadAllAsync(ct)).Where(s => s.Name != scenario.Name).ToList();
            all.Add(scenario);
            await WriteAllAsync(all, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            var remaining = all.Where(s => s.Name != name).ToList();
            if (remaining.Count == all.Count)
            {
                return false;
            }

            await WriteAllAsync(remaining, ct);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<CustomScenario>> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<List<CustomScenario>>(stream, JsonOptions, ct) ?? [];
    }

    private async Task WriteAllAsync(List<CustomScenario> scenarios, CancellationToken ct)
    {
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, scenarios, JsonOptions, ct);
    }
}
