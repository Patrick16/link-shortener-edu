using Common;
using Infrastructure;

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
        builder.Services.AddHttpClient<IGeoIpResolver, IpApiGeoIpResolver>(client =>
        {
            client.BaseAddress = new Uri("http://ip-api.com");
        });

        builder.Services.AddHostedService<ClickTrackedConsumer>();
        return builder;
    }
}
