using System.Text.Json;
using ControlApi.Models;

namespace ControlApi.Services.DataSources;

// The data-pool preload half of a traffic run - fetching real records from the app up front so a
// run's steps can use varied data instead of one fixture value every iteration. Pure move out of
// TrafficService, same behavior - see .notes/PLAN.md's Simplification Backlog item 3.
public sealed class DataPoolFetcher
{
    // What a run's optional DataPoolRequest can preload - see DataSourceDefinition for why this is
    // separate from TrafficService.EndpointRegistry (a bulk paginated read done once before the run
    // starts, not a per-iteration call). ProducesVar "hash" is flow.js's own variable name, the same
    // one link-api.create's Produces entry writes to, so a preloaded pool and a live Create step
    // both feed the exact same downstream steps without flow.js needing to treat them differently.
    public static readonly IReadOnlyList<DataSourceDefinition> Registry =
    [
        new(
            "link-api.links", "link-api", "hash",
            "Existing short links, paged from GET /Links - use this to make a Resolve-only sequence hit varied real records instead of the same one fixture link every iteration"),
    ];

    // Reused across every FetchAsync call rather than one-per-call - this instance is itself a
    // singleton service, so that's exactly the lifetime HttpClient is meant to be used at (a fresh
    // instance per call risks socket exhaustion under load, which is ironic for a load testing tool
    // to hit).
    private readonly HttpClient _httpClient = new();
    private readonly ILogger<DataPoolFetcher> _logger;

    public DataPoolFetcher(IConfiguration configuration, ILogger<DataPoolFetcher> logger)
    {
        _logger = logger;

        // LinkApi's GET /Links requires either a per-user Bearer token or this shared secret (see
        // src/backend/Shared/WebDefaults/InternalApiKeyAuthenticationHandler.cs) - control-api has
        // no user session/JWT of its own, and the whole point of this call is an unscoped sample
        // across every user's links, which a per-user token couldn't grant even if control-api
        // somehow had one. Header name/config key are duplicated as plain strings rather than a
        // shared reference, deliberately - sandbox/ (this tool) and src/ (the product) are kept
        // decoupled on purpose (see docs/repository-map.md); the docker-compose.yml internal-api-key anchor
        // is what actually keeps the two sides' secret value in sync, not a shared constant.
        if (configuration["Internal:ApiKey"] is { } internalApiKey)
        {
            _httpClient.DefaultRequestHeaders.Add("X-Internal-Api-Key", internalApiKey);
        }
    }

    // Dispatches on the source id so adding a second DataSourceDefinition later only means adding a
    // case here, not touching TrafficService.RunTrafficAsync. An id that reaches here without a
    // matching case (only possible if Registry grows a source this switch hasn't been taught yet)
    // yields an empty pool rather than throwing - a run should still proceed using the fixture
    // fallback.
    public Task<IReadOnlyList<string>> FetchAsync(DataSourceDefinition source, int count, bool nginxBypassed, Func<TrafficProgress, Task> onProgress, CancellationToken ct) =>
        source.Id switch
        {
            "link-api.links" => FetchLinkHashesAsync(count, nginxBypassed, onProgress, ct),
            _ => Task.FromResult<IReadOnlyList<string>>([]),
        };

    // Pages through the real GET /Links (the same nginx-bypass-aware URL k6 itself would use) until
    // `count` hashes are collected or the app runs out of links, pushing a "preparing" progress
    // update after every page so a large pool doesn't look like a hang. Fewer real links existing
    // than requested isn't an error - the pool just ends up smaller, which the caller can see in
    // PreparedCount ending below PreparedTarget in the final push. nginxBypassed comes in as a
    // parameter - the same value RunTrafficAsync captured once for the whole run, not a second,
    // independent read of the toggle that could disagree with it (see RunTrafficAsync's comment).
    private async Task<IReadOnlyList<string>> FetchLinkHashesAsync(int count, bool nginxBypassed, Func<TrafficProgress, Task> onProgress, CancellationToken ct)
    {
        var baseUrl = $"http://{(nginxBypassed ? "link-api:8080" : "nginx:8082")}";
        var hashes = new List<string>(count);
        var page = 1;

        while (hashes.Count < count)
        {
            HttpResponseMessage response;
            try
            {
                // The default HttpClient request timeout (100s) expires as a TaskCanceledException/
                // OperationCanceledException, not HttpRequestException - without also catching that,
                // a partitioned/100%-packet-loss link-api (this app's own chaos "Partition" control)
                // hangs here for ~100s and then throws uncaught, aborting the entire traffic run
                // instead of the graceful "pool ends up smaller, run still starts with the fixture
                // fallback" behavior this method's own design intends.
                response = await _httpClient.GetAsync($"{baseUrl}/Links?page={page}", ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Fetching link pool page {Page} failed - stopping with {Count} collected", page, hashes.Count);
                break;
            }

            using var responseDisposer = response;
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Fetching link pool page {Page} returned {StatusCode} - stopping with {Count} collected",
                    page, response.StatusCode, hashes.Count);
                break;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var items = doc.RootElement.GetProperty("items");
            if (items.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var item in items.EnumerateArray())
            {
                hashes.Add(item.GetProperty("shortenLink").GetString()!);
                if (hashes.Count >= count)
                {
                    break;
                }
            }

            await onProgress(new TrafficProgress(
                0, 0, (int)Math.Min(100, hashes.Count * 100.0 / count), 0, 0, 0,
                Phase: "preparing", PreparedCount: hashes.Count, PreparedTarget: count));

            if (page >= doc.RootElement.GetProperty("totalPages").GetInt32())
            {
                break;
            }

            page++;
        }

        return hashes;
    }
}
