using Common;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;

namespace Infrastructure;

public static class GeoIpExtensions
{
    // TrafficService and ReportingService are two independent fan-out consumers of the same
    // ClickTrackedEvent, each needing to resolve geo-IP for the same click against ip-api.com's
    // shared ~45 req/min free tier - wrapping IpApiGeoIpResolver in CachingGeoIpResolver, pointed
    // at the same Redis instance/InstanceName (Redis__InstanceName=GeoIpCache), lets the two
    // consumers share one cache instead of each hitting ip-api.com separately (see
    // CachingGeoIpResolver's own comment). This registration used to be copy-pasted identically
    // into both services' extension classes, each commenting that it had to be kept in sync with
    // the other by hand - factored out here so there's exactly one place it can drift from itself
    // (found during review).
    public static IHostApplicationBuilder AddGeoIpResolution(this IHostApplicationBuilder builder)
    {
        builder.AddRedisDistributedCache();
        builder.Services.AddHttpClient<IpApiGeoIpResolver>(client =>
            {
                client.BaseAddress = new Uri("http://ip-api.com");
            })
            // ip-api.com's free tier is both rate-limited and a real network hop with a 3s
            // timeout per call (see IpApiGeoIpResolver) - a dead or throttled dependency would
            // otherwise make every click pay that full 3s before falling back to "no geo data"
            // (CachingGeoIpResolver's own catch). Microsoft.Extensions.Http.Resilience wires this
            // straight into the HttpClient pipeline, unlike Redis/gRPC/RabbitMQ below which have
            // no such built-in hook and need an explicit ExecuteAsync at the call site.
            .AddResilienceHandler("geo-ip-circuit-breaker", resilience => resilience.AddCircuitBreaker(new()
            {
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<Exception>(ex => ex is not OperationCanceledException)
                    .HandleResult(response => (int)response.StatusCode >= 500),
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(15),
            }));
        builder.Services.AddSingleton<IGeoIpResolver>(sp => new CachingGeoIpResolver(
            sp.GetRequiredService<IpApiGeoIpResolver>(),
            sp.GetRequiredService<IDistributedCache>(),
            sp.GetRequiredService<ILogger<CachingGeoIpResolver>>(),
            sp.GetRequiredKeyedService<ResiliencePipeline>(Constants.RedisCircuitBreakerKey)));

        return builder;
    }
}
