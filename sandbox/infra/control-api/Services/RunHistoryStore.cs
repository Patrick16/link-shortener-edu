using System.Text.Json;
using System.Text.RegularExpressions;
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
                    var summary = await ReadSummaryAsync(file, ct);
                    if (summary is not null)
                    {
                        summaries.Add(summary);
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

    // RunSummary's whole reason for existing (see its own doc comment) is avoiding a full
    // JsonSerializer.DeserializeAsync<RunSnapshot> here - that would materialize every field into a
    // .NET object graph, including RawOutput (k6's raw stdout, potentially large) and every
    // connection-stats/resource-maxima/trace-hop collection, just to read 6 scalars out of it. With
    // MaxRetained = 200 files and ListAsync called on every RunHistoryPanel refresh (mount, and
    // after every completed run), that was 200 full deserializations of documents most of whose
    // content is thrown away immediately. JsonDocument still reads/parses every byte, but leaves
    // string/number values as unmaterialized JsonElement slices into the parsed buffer until
    // something actually calls GetString()/GetInt64() on them - so a big RawOutput blob is skipped
    // over, never turned into a .NET string, since nothing here ever reads that property.
    internal static async Task<RunSummary?> ReadSummaryAsync(string file, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;

        if (!root.TryGetProperty("Id", out var idProp) || idProp.GetString() is not { } id)
        {
            return null;
        }

        var timestamp = root.GetProperty("Timestamp").GetDateTimeOffset();
        var scenario = root.GetProperty("Request").GetProperty("Scenario").GetString() ?? string.Empty;
        var report = root.GetProperty("Report");
        return new RunSummary(
            id,
            timestamp,
            scenario,
            report.GetProperty("HttpRequests").GetInt64(),
            report.GetProperty("FailedRequests").GetInt64(),
            report.GetProperty("ExitCode").GetInt64(),
            report.GetProperty("HttpRequestRate").GetDouble());
    }

    public async Task<RunSnapshot?> GetAsync(string id, CancellationToken ct)
    {
        // SaveAsync/DeleteAsync/ClearAsync all take _lock before touching files - this used to be
        // the one exception, checking File.Exists then File.OpenRead with no lock at all. A run
        // completing (SaveAsync's stale-file cleanup) or an explicit delete/clear landing between
        // those two calls is a classic TOCTOU: File.OpenRead throws FileNotFoundException, which
        // nothing here or in the minimal-API route catches, so the request fails with an unhandled
        // 500 instead of the intended 404.
        await _lock.WaitAsync(ct);
        try
        {
            var path = PathFor(id);
            if (path is null || !File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<RunSnapshot>(stream, JsonOptions, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(RunSnapshot snapshot, CancellationToken ct)
    {
        // Ids here are always server-generated (see Program.cs) - an invalid one reaching this far
        // would be a programming bug, not user input, so this throws instead of failing quietly.
        var path = PathFor(snapshot.Id) ?? throw new ArgumentException($"Invalid run id: {snapshot.Id}", nameof(snapshot));

        await _lock.WaitAsync(ct);
        try
        {
            await using (var stream = File.Create(path))
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
            if (path is null || !File.Exists(path))
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

    // Returns how many files it failed to delete - a single bad file (permissions, an unexpected
    // I/O error) used to abort the whole loop, leaving everything not yet reached still on disk
    // while everything before it was already gone, and surfaced as an unhandled 500 instead of a
    // clean partial-success report. Same per-file isolation as ListAsync's own catch, just for
    // delete instead of read - one failure no longer stops the rest of the clear.
    public async Task<int> ClearAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var failedCount = 0;
            foreach (var file in Directory.GetFiles(_dir, "*.json"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failedCount++;
                }
            }

            return failedCount;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Run ids are always server-generated as "yyyyMMdd-HHmmss-fff-<6 hex chars>" (see Program.cs) -
    // this allowlist is deliberately looser than that exact shape (letters/digits/hyphens only, no
    // length constraint) so it isn't brittle against a future format tweak, while still rejecting
    // every character path traversal actually needs: no `.`, `/`, `\`, or `%` (URL-encoded
    // separators) can ever appear. Validating here - not just wherever the id came in from - closes
    // off traversal (`../`, an absolute path, anything Path.Combine would otherwise resolve outside
    // _dir) for every caller of this class, including a future one, rather than relying on every
    // call site to sanitize its own input. control-api has no authentication and control-api's own
    // File.Delete calls (SaveAsync's retention cleanup, DeleteAsync, ClearAsync) make this
    // destructive, not just a read - an id built from a caller-controlled string reaching
    // File.Delete unsanitized is an arbitrary-file-deletion primitive, not merely an unusual
    // identifier.
    private static readonly Regex ValidIdPattern = new(@"^[A-Za-z0-9-]+$", RegexOptions.Compiled);

    private string? PathFor(string id) => ValidIdPattern.IsMatch(id) ? Path.Combine(_dir, $"{id}.json") : null;
}
