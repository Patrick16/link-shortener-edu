using System.Threading.RateLimiting;
using Common;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.RateLimiting;

namespace Infrastructure;

public static class GeoIpExtensions
{
    // Bulkhead isolation (resilience backlog item 15) - caps how many ip-api.com calls this
    // process has in flight at once, independent of the circuit breaker below (that one only trips
    // on a FAILURE ratio; this one protects against the dependency merely being slow and tying up
    // this process's connection/thread-pool resources on every concurrent click). Configurable so
    // it can be dialed live via control-api's "geoip-bulkhead" capability (sets
    // GEOIP_BULKHEAD_LIMIT, same env-var + container-recreate shape as rabbitmq-prefetch/
    // npgsql-pool-size - read once at startup, not a live-reloadable setting).
    private const int DefaultMaxConcurrentRequests = 10;
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
        var maxConcurrentRequests = int.TryParse(builder.Configuration["GeoIp:MaxConcurrentRequests"], out var configured)
            ? configured
            : DefaultMaxConcurrentRequests;

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
            .AddResilienceHandler("geo-ip-circuit-breaker", (resilience, context) =>
            {
                var logger = context.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("GeoIpBulkhead");
                resilience
                    // Checked BEFORE the circuit breaker in the pipeline (strategies run in the
                    // order added) - a call this process was never going to let through due to
                    // concurrency shouldn't also count against the breaker's own failure ratio.
                    // AddRateLimiter (not the simpler AddConcurrencyLimiter(permitLimit, queueLimit)
                    // overload) is what's needed here purely to get OnRejected - that overload only
                    // takes a System.Threading.RateLimiting.ConcurrencyLimiterOptions, which has no
                    // rejection hook of its own; leaving RateLimiter null below falls back to the
                    // exact same ConcurrencyLimiter, built from DefaultRateLimiterOptions.
                    .AddRateLimiter(new RateLimiterStrategyOptions
                    {
                        DefaultRateLimiterOptions = new ConcurrencyLimiterOptions
                        {
                            PermitLimit = maxConcurrentRequests,
                            // Reject immediately rather than queue - same load-shedding choice as
                            // RedirectApi's request-budget feature (see Redirect:RequestBudgetMs):
                            // a lookup that can't start right now is better abandoned (the
                            // catch-all in IpApiGeoIpResolver.ResolveAsync already treats any
                            // exception, including this rejection, as "no geo data") than queued
                            // behind others and risking this consumer's own RabbitMQ batch timeout.
                            QueueLimit = 0,
                        },
                        OnRejected = args =>
                        {
                            logger.LogWarning(
                                "Geo-IP concurrency limit ({Limit} in flight) rejected a call - treating as no geo data",
                                maxConcurrentRequests);
                            ResilienceMetrics.BulkheadRejections.Add(1, new KeyValuePair<string, object?>("dependency", "geo-ip"));
                            return default;
                        },
                    })
                    .AddCircuitBreaker(new()
                    {
                        ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                            .Handle<Exception>(ex => ex is not OperationCanceledException)
                            .HandleResult(response => (int)response.StatusCode >= 500),
                        FailureRatio = 0.5,
                        MinimumThroughput = 10,
                        SamplingDuration = TimeSpan.FromSeconds(30),
                        BreakDuration = TimeSpan.FromSeconds(15),
                    });
            });
        builder.Services.AddSingleton<IGeoIpResolver>(sp => new CachingGeoIpResolver(
            sp.GetRequiredService<IpApiGeoIpResolver>(),
            sp.GetRequiredService<IDistributedCache>(),
            sp.GetRequiredService<ILogger<CachingGeoIpResolver>>(),
            sp.GetRequiredKeyedService<ResiliencePipeline>(Constants.RedisCircuitBreakerKey)));

        return builder;
    }
}
