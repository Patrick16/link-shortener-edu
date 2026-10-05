using Common;
using Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace TrafficService;

// TrafficService-only wiring - the parts of Program.cs that no other service shares.
public static class TrafficServiceExtensions
{
    // Click enrichment (user agent + GeoIP) and the Mongo store for the resulting metadata, fed by
    // the click.tracked consumer.
    public static WebApplicationBuilder AddClickTracking(this WebApplicationBuilder builder)
    {
        var mongoConnectionString = builder.Configuration.GetConnectionString(Constants.MongoDbConnectionString);
        builder.Services.AddSingleton<IClickMetaStore>(_ => new MongoClickMetaStore(mongoConnectionString!));
        builder.Services.AddSingleton<IUserAgentParser, UaParserUserAgentParser>();

        // IGeoIpResolver is CachingGeoIpResolver wrapping the real IpApiGeoIpResolver - see its own
        // comment. ReportingService registers the identical wrapping, pointed at the same Redis
        // instance/InstanceName (Redis__InstanceName=GeoIpCache in docker-compose.yml), so the two
        // independent click.tracked consumers share one cache instead of each hitting ip-api.com's
        // rate-limited endpoint separately for the same click.
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
