using Common;
using Infrastructure;
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

        // See GeoIpExtensions.AddGeoIpResolution's own comment - shared with ReportingService so
        // both independent click.tracked consumers hit one Redis-backed cache instead of each
        // hitting ip-api.com's rate-limited endpoint separately for the same click.
        builder.AddGeoIpResolution();

        // Registered as itself first, then wrapped as the IHostedService - see
        // ShortenerServiceExtensions's identical comment: MessagingGrpcService (gRPC mode's server
        // side) also needs to resolve ClickTrackedConsumer directly.
        builder.Services.AddSingleton<ClickTrackedConsumer>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ClickTrackedConsumer>());
        return builder;
    }

    // Rolling partition maintenance for clicks_db's partitioned clicks table - see
    // PartitionMaintenanceWorker's own comment for what it does and why it lives here rather than
    // as a separate deployable (this service already owns clicks_db and its migration).
    public static WebApplicationBuilder AddPartitionMaintenance(this WebApplicationBuilder builder)
    {
        builder.Services.AddHostedService<PartitionMaintenanceWorker>();
        return builder;
    }
}
