using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
        });
        builder.Services.AddSingleton<IGeoIpResolver>(sp => new CachingGeoIpResolver(
            sp.GetRequiredService<IpApiGeoIpResolver>(),
            sp.GetRequiredService<IDistributedCache>(),
            sp.GetRequiredService<ILogger<CachingGeoIpResolver>>()));

        return builder;
    }
}
