using Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Infrastructure;

// Mirrors ClickHouseExtensions's writer/reader split for clicks_meta_db - write side (TrafficService,
// the sole owner) and read side (ReportingApi) each get their own DI registration, so neither
// service can accidentally depend on the half of the capability it doesn't use.
public static class MongoExtensions
{
    // Mirrors GeoIpExtensions.DefaultMaxConcurrentRequests's own shape - a plain constant default,
    // overridable via config, with the control-api dial applying it through the same env-var +
    // force-recreate mechanism every other numeric knob in this project uses.
    private const int DefaultHedgeDelayMs = 15;

    // Write side (TrafficService) - the only service that ever upserts into clicks_meta_db.
    public static IHostApplicationBuilder AddMongoClickMetaWriter(this IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString(Constants.MongoDbConnectionString);
        builder.Services.AddSingleton<IClickMetaStore>(_ => new MongoClickMetaStore(connectionString!));
        return builder;
    }

    // Read side (ReportingApi) - the only service that ever queries clicks_meta_db. Wraps the
    // ordinary replica-set-aware client in HedgedClickMetaQueryService, which races GetRecentAsync
    // against two of rs0's members directly (bypassing replica-set discovery - see that class's
    // own header comment for why this needs its own direct-to-node clients rather than just reusing
    // whichever node readPreference happens to pick). Falls back to the plain client whenever rs0
    // doesn't currently have all 3 members (the Mongo-replicas toggle is off) - nothing to hedge
    // against with only mongo1 up.
    public static IHostApplicationBuilder AddMongoClickMetaReader(this IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString(Constants.MongoDbConnectionString)!;
        var hedgeDelayMs = builder.Configuration.GetValue("Mongo:HedgeDelayMs", DefaultHedgeDelayMs);

        builder.Services.AddSingleton<IClickMetaQueryService>(sp =>
        {
            var fallback = new MongoClickMetaQueryService(connectionString);

            var url = MongoUrl.Create(connectionString);
            if (url.Servers.Count() < 3)
            {
                return fallback;
            }

            // Skips the first server (mongo1, the primary) on purpose - see this reader's own
            // comment for why the primary stays out of the hedge pool. directConnection=true is
            // required here: without it, the Mongo driver would discover the whole replica set
            // from this one seed and route the query via its OWN server-selection logic again,
            // silently undoing the "pin this call to exactly this physical node" point of hedging.
            var hedgeNodes = url.Servers.Skip(1)
                .Select(server => (IClickMetaQueryService)new MongoClickMetaQueryService(
                    $"mongodb://{server.Host}:{server.Port}/{url.DatabaseName}?directConnection=true"))
                .ToList();

            return new HedgedClickMetaQueryService(
                fallback, hedgeNodes, TimeSpan.FromMilliseconds(hedgeDelayMs),
                sp.GetRequiredService<ILogger<HedgedClickMetaQueryService>>());
        });

        return builder;
    }
}
