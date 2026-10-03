using System.Text.Json;
using ControlApi.Models;

namespace ControlApi.Services;

// Same shape as ScenarioStore - a flat JSON file under Scenarios:DataDir (the shared control-api
// data dir, same named volume ScenarioStore/RunHistoryStore already use), not a database. A
// handful of hand-named infra configs for one local user, not shared multi-writer state.
public class PresetStore : IPresetStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public PresetStore(IConfiguration configuration)
    {
        var dataDir = configuration["Scenarios:DataDir"] ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "presets.json");
    }

    public async Task<IReadOnlyList<Preset>> ListAsync(CancellationToken ct)
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

    public async Task SaveAsync(Preset preset, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = (await ReadAllAsync(ct)).Where(p => p.Name != preset.Name).ToList();
            all.Add(preset);
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
            var remaining = all.Where(p => p.Name != name).ToList();
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

    private async Task<List<Preset>> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<List<Preset>>(stream, JsonOptions, ct) ?? [];
    }

    private async Task WriteAllAsync(List<Preset> presets, CancellationToken ct)
    {
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, presets, JsonOptions, ct);
    }
}
