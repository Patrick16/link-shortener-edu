using Common;
using Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace ReportingService;

// ReportingService-only wiring - the parts of Program.cs that no other service shares. Needs its
// own IUserAgentParser/IGeoIpResolver registrations (same concrete types TrafficService already
// uses) because it's an independent second consumer of the raw ClickTrackedEvent, not a reader of
// TrafficService's already-parsed ClickMeta - each read model is built straight from the source
// event, not from another service's derived data.
public static class ReportingServiceExtensions
{
    public static WebApplicationBuilder AddReportIngestion(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IUserAgentParser, UaParserUserAgentParser>();

        // IGeoIpResolver is CachingGeoIpResolver wrapping the real IpApiGeoIpResolver - see its own
        // comment. TrafficService registers the identical wrapping, pointed at the same Redis
        // instance/InstanceName (Redis__InstanceName=GeoIpCache in docker-compose.yml), so this
        // consumer and TrafficService's share one cache instead of each independently hitting
        // ip-api.com's rate-limited endpoint for the same click (found during review - this service
        // originally doubled TrafficService's request rate against that shared budget).
        builder.AddRedisDistributedCache();
        builder.Services.AddHttpClient<IpApiGeoIpResolver>(client =>
        {
            client.BaseAddress = new Uri("http://ip-api.com");
        });
        builder.Services.AddSingleton<IGeoIpResolver>(sp => new CachingGeoIpResolver(
            sp.GetRequiredService<IpApiGeoIpResolver>(), sp.GetRequiredService<IDistributedCache>()));

        builder.Services.AddHostedService<ClickTrackedConsumer>();
        return builder;
    }
}
