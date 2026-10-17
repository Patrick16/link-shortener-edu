using System.Globalization;
using ControlApi.Models;

namespace ControlApi.Services;

public sealed class HaproxyService(ILogger<HaproxyService> logger) : IHaproxyService
{
    // Same "new HttpClient() held for this singleton's lifetime" pattern TrafficService.cs already
    // uses - no IHttpClientFactory machinery needed for one fixed internal URL.
    private readonly HttpClient _httpClient = new();

    // HAProxy's own built-in CSV stats endpoint (see infra/haproxy/haproxy.cfg's "observability"
    // frontend) - a plain HTTP call over the docker network, unlike PgcatService/RabbitMqService's
    // docker-exec-based capabilities. No auth configured on that endpoint (same no-auth dev posture
    // as the rest of this stack), so a direct GET is all this needs.
    //
    // CSV columns (confirmed against a real haproxy before parsing anything): pxname,svname,qcur,
    // qmax,scur,smax,slim,stot,... (scur at index 4) ...,status,... (status at index 17). The header
    // line starts with "# " and every other proxy's own FRONTEND/BACKEND aggregate rows are skipped
    // - only pgcat_back's individual server rows (pgcat1/pgcat2/pgcat3, haproxy.cfg's
    // server-template naming) are what this capability reports.
    public async Task<HaproxyStats?> GetHaproxyStatsAsync(CancellationToken ct)
    {
        string csv;
        try
        {
            csv = await _httpClient.GetStringAsync("http://haproxy:8405/stats;csv", ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not reach haproxy's stats endpoint");
            return null;
        }

        var servers = new List<HaproxyServerStats>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            var f = line.Split(',');
            if (f.Length < 18 || f[0] != "pgcat_back" || f[1] is "BACKEND" or "FRONTEND")
            {
                continue;
            }

            // haproxy.cfg's server-template reserves 6 slots for today's 3 replicas (2x headroom -
            // see that file's own comment), so slots 4-6 never get a DNS answer and sit in status
            // "MAINT (resolution)" forever - confirmed live via this exact CSV. That's a reserved-
            // but-unused slot, not a failed backend; surfacing it as "DOWN" (which an earlier version
            // of this method did, by collapsing every non-UP status into one bool) is what made
            // healthy headroom look like 3 dead pgcat instances. Skip it entirely instead.
            if (f[17].StartsWith("MAINT", StringComparison.Ordinal))
            {
                continue;
            }

            servers.Add(new HaproxyServerStats(f[1], f[17] == "UP", int.Parse(f[4], CultureInfo.InvariantCulture)));
        }

        return new HaproxyStats(servers);
    }
}
