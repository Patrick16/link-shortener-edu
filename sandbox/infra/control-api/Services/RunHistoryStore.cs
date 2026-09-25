using System.Text.Json;
using ControlApi.Models;

namespace ControlApi.Services;

// One file per run (not one growing array like ScenarioStore) - runs accumulate continuously
// during a session, so a file-per-run avoids a read-modify-write of an ever-larger single file on
// every save. Lives on the same control-api-data volume as saved scenarios, so history survives a
// control-api rebuild too. Retention is capped (oldest deleted past MaxRetained) since this is a
// local dev tool, not a real observability system - unbounded growth isn't worth guarding against
// forever, but an interactive session left running for days shouldn't fill the volume either.
public class RunHistoryStore : IRunHistoryStore
{
    private const int MaxRetained = 200;
    private readonly string _dir;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public RunHistoryStore(IConfiguration configuration)
    {
        var dataDir = configuration["Scenarios:DataDir"] ?? Path.Combine(AppContext.BaseDirectory, "data");
        _dir = Path.Combine(dataDir, "runs");
        Directory.CreateDirectory(_dir);
    }

    public async Task<IReadOnlyList<RunSummary>> ListAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var summaries = new List<RunSummary>();
            foreach (var file in Directory.GetFiles(_dir, "*.json"))
            {
                try
                {
                    await using var stream = File.OpenRead(file);
                    var snapshot = await JsonSerializer.DeserializeAsync<RunSnapshot>(stream, JsonOptions, ct);
                    if (snapshot is not null)
                    {
                        summaries.Add(new RunSummary(snapshot.Id, snapshot.Timestamp, snapshot.Request.Scenario, snapshot.Report.HttpRequests, snapshot.Report.FailedRequests, snapshot.Report.ExitCode, snapshot.Report.HttpRequestRate));
                    }
                }
                catch (JsonException)
                {
                    // A partially-written or corrupt file shouldn't take down the whole list.
                }
            }

            return summaries.OrderByDescending(s => s.Timestamp).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<RunSnapshot?> GetAsync(string id, CancellationToken ct)
    {
        var path = PathFor(id);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<RunSnapshot>(stream, JsonOptions, ct);
    }

    public async Task SaveAsync(RunSnapshot snapshot, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using (var stream = File.Create(PathFor(snapshot.Id)))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, ct);
            }

            // Ids are timestamp-prefixed (see Program.cs), so a plain filename sort is chronological.
            var stale = Directory.GetFiles(_dir, "*.json").OrderByDescending(f => f).Skip(MaxRetained);
            foreach (var file in stale)
            {
                File.Delete(file);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var path = PathFor(id);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            foreach (var file in Directory.GetFiles(_dir, "*.json"))
            {
                File.Delete(file);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private string PathFor(string id) => Path.Combine(_dir, $"{id}.json");
}
