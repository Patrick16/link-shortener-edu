using System.Text.Json;
using ControlApi.Models;

namespace ControlApi.Services.K6;

// Pure move out of TrafficService - this was already a static, self-contained method (see
// .notes/PLAN.md's Simplification Backlog item 3); living in its own file/class just makes it
// directly [Theory]-testable without going through the full Docker-dependent RunTrafficAsync path.
public static class K6SummaryParser
{
    // Only status codes k6 was explicitly told to track (via a trivial always-passing threshold on
    // "http_reqs{status:X}" - see each k6-scripts/*.js) end up as separate entries in the summary
    // JSON at all; anything not listed here still counts toward the overall http_reqs total, it
    // just won't be broken out by code. "0" is k6's own status for a request that got no HTTP
    // response whatsoever (connection refused/reset, or timed out) - genuinely different from a
    // server responding with an error code, so it gets its own human-readable label instead of "0".
    private static readonly IReadOnlyList<(string Code, string Label)> TrackedStatusCodes =
    [
        ("200", "200 OK"),
        ("204", "204 No Content"),
        ("302", "302 Found"),
        ("401", "401 Unauthorized"),
        ("403", "403 Forbidden"),
        ("404", "404 Not Found"),
        ("409", "409 Conflict"),
        ("500", "500 Internal Server Error"),
        ("502", "502 Bad Gateway"),
        ("503", "503 Service Unavailable"),
        ("504", "504 Gateway Timeout"),
        ("0", "No response (connection error / timeout)"),
    ];

    public static TrafficReport Parse(string json, string scenario, IReadOnlyList<string> stepIds, long exitCode, string rawOutput)
    {
        using var doc = JsonDocument.Parse(json);
        var metrics = doc.RootElement.GetProperty("metrics");

        long GetCount(string metric) =>
            metrics.TryGetProperty(metric, out var m) && m.TryGetProperty("count", out var c) ? c.GetInt64() : 0;
        double GetRate(string metric) =>
            metrics.TryGetProperty(metric, out var m) && m.TryGetProperty("rate", out var r) ? r.GetDouble() : 0;
        double GetValue(string metric) =>
            metrics.TryGetProperty(metric, out var m) && m.TryGetProperty("value", out var v) ? v.GetDouble() : 0;
        // http_req_failed is a k6 Rate metric: "passes" is the count of samples where the request
        // was considered failed (value 1), not a count of successes - the passes/fails naming is
        // generic to Rate metrics (also used by "checks", where 1 *does* mean success) and doesn't
        // flip meaning per-metric, so this needs to be read deliberately rather than by the names.
        long GetFailedCount(string metric) =>
            metrics.TryGetProperty(metric, out var m) && m.TryGetProperty("passes", out var p) ? p.GetInt64() : 0;

        LatencyStats? duration = null;
        if (metrics.TryGetProperty("http_req_duration", out var d))
        {
            double Field(string name) => d.TryGetProperty(name, out var v) ? v.GetDouble() : 0;
            duration = new LatencyStats(Field("avg"), Field("min"), Field("med"), Field("max"), Field("p(90)"), Field("p(95)"));
        }

        var checks = new List<CheckResult>();
        if (doc.RootElement.TryGetProperty("root_group", out var rootGroup) && rootGroup.TryGetProperty("checks", out var checksObj))
        {
            foreach (var check in checksObj.EnumerateObject())
            {
                var passes = check.Value.TryGetProperty("passes", out var p) ? p.GetInt32() : 0;
                var fails = check.Value.TryGetProperty("fails", out var f) ? f.GetInt32() : 0;
                checks.Add(new CheckResult(check.Name, passes, fails));
            }
        }

        // Each request is tagged with its originating step id (see flow.js), and flow.js declares a
        // threshold for every (stepId, trackedStatusCode) pair up front - only tag combinations with
        // a declared threshold get retained/exported by k6 at all, and only ones that actually
        // occurred (Count > 0) are worth showing.
        var statusByEndpoint = stepIds
            .Select(id => new EndpointStatusBreakdown(
                id,
                TrackedStatusCodes
                    .Select(sc => new StatusCount(sc.Label, GetCount($"http_reqs{{step:{id},status:{sc.Code}}}")))
                    .Where(sc => sc.Count > 0)
                    .OrderByDescending(sc => sc.Count)
                    .ToList()))
            .Where(e => e.StatusCounts.Count > 0)
            .ToList();

        return new TrafficReport(
            scenario,
            exitCode,
            GetCount("http_reqs"),
            GetRate("http_reqs"),
            GetFailedCount("http_req_failed"),
            GetValue("http_req_failed"),
            GetCount("iterations"),
            GetRate("iterations"),
            (int)GetValue("vus_max"),
            duration,
            checks,
            statusByEndpoint,
            rawOutput);
    }
}
