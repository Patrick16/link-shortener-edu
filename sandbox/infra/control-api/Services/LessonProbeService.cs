using System.Diagnostics;
using System.Text;
using ControlApi.Models;

namespace ControlApi.Services;

// Backs a chaos-trigger lesson's own probe (see ChaosProbeInput's own comment). Looks the endpoint
// up in TrafficService.EndpointRegistry - the exact same ids/templates a load-test step already
// uses - rather than knowing any route shape itself, same "server-side source of truth, dumb
// caller" split flow.js has with that registry.
public class LessonProbeService(ITrafficService traffic, IHttpClientFactory httpClientFactory, IInfraToggleService infra) : ILessonProbeService
{
    public async Task<ChaosProbeResult> RunAsync(ChaosProbeInput input, CancellationToken ct)
    {
        var endpoint = traffic.ListKnownEndpoints().FirstOrDefault(e => e.Id == input.EndpointId)
            ?? throw new ArgumentOutOfRangeException(nameof(input), input.EndpointId, "Unknown endpointId for a chaos probe.");

        var status = await infra.GetInfraStatusAsync(ct);
        var baseUrl = ServiceBaseUrls.Resolve(endpoint.ServiceId, status.NginxBypassed);
        var client = httpClientFactory.CreateClient();
        // A degraded dependency (100% packet loss/partition) can hang a request far past
        // HttpClient's own 100s default - a single probe call waiting that long would make the
        // whole lesson feel frozen, not just slow. Counting a timeout as a failure this fast is
        // itself an honest answer for "did this request succeed", not an artifact of the probe.
        client.Timeout = TimeSpan.FromSeconds(8);

        var totalRequests = Math.Max(1, input.Count);
        var latencies = new List<double>(totalRequests);
        var successCount = 0;

        for (var i = 0; i < totalRequests; i++)
        {
            // A fresh random suffix per attempt, same reason flow.js seeds a new `rand` per
            // iteration - a probe that always posted the exact same email/originalLink would
            // either collide (auth-api.register) or mask a cache hit as a "success" that didn't
            // actually need the degraded dependency at all.
            var rand = Guid.NewGuid().ToString("N")[..8];
            var path = Substitute(endpoint.PathTemplate, rand);
            var body = Substitute(endpoint.BodyTemplate, rand);

            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), $"{baseUrl}{path}");
                if (body is not null)
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }

                using var response = await client.SendAsync(request, ct);
                stopwatch.Stop();
                latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                if ((int)response.StatusCode is >= 200 and < 400)
                {
                    successCount++;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A connection refused/reset/timeout against a genuinely stopped/degraded
                // dependency IS the failure this probe exists to detect, not an error to
                // propagate - counted exactly like any other non-2xx/3xx response.
                stopwatch.Stop();
                latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
            }

            if (i < totalRequests - 1 && input.IntervalMs > 0)
            {
                await Task.Delay(input.IntervalMs, ct);
            }
        }

        var failureCount = totalRequests - successCount;
        return new ChaosProbeResult(
            totalRequests,
            successCount,
            failureCount,
            Math.Round(failureCount * 100.0 / totalRequests, 1),
            Math.Round(Percentile95(latencies), 1));
    }

    // Only the two placeholders every current probe target's templates actually use
    // (auth-api.register's email/password, link-api.create's originalLink) - not flow.js's full
    // variable store (hash/token from earlier steps), since a probe is always a single isolated
    // call with nothing produced before it.
    private static string? Substitute(string? template, string rand) =>
        template
            ?.Replace("{{email}}", $"probe-{rand}@example.com")
            .Replace("{{password}}", "Password123!")
            .Replace("{{originalLink}}", $"https://example.com/probe-{rand}");

    private static double Percentile95(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        var index = Math.Clamp((int)Math.Ceiling(0.95 * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }
}
