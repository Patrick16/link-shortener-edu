using Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
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
    // against two of rs0's current secondaries directly (bypassing replica-set discovery - see
    // that class's own header comment for why this needs its own direct-to-node clients rather
    // than just reusing whichever node readPreference happens to pick). Falls back to the plain
    // client whenever rs0 doesn't currently have all 3 members (the Mongo-replicas toggle is off) -
    // nothing to hedge against with only one node up, whichever one that is.
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

            // Excludes whichever node is CURRENTLY the real primary - checked live via each
            // node's own `hello` reply, not by trusting MONGO_HOSTS's list position. Found in
            // review: this project's own PLAN.md already has a precedent (the 2026-10-06 Mongo
            // backup-script fix) where mongo2, not mongo1, was the real primary after a failover -
            // an earlier version of this code assumed "first in the list" and would have silently
            // hedged against the primary instead of the two secondaries once that happened here
            // too. directConnection=true is required on each candidate: without it, the Mongo
            // driver would discover the whole replica set from that one seed and route the query
            // via its OWN server-selection logic again, silently undoing the "pin this call to
            // exactly this physical node" point of hedging.
            //
            // readPreference=secondary is just as required - found live (2026-11-06, lesson 5.1's
            // first real run): a driver's default ReadPreference is Primary regardless of
            // directConnection, so without this every single hedge query failed with
            // MongoNotPrimaryException the moment it reached a node that (correctly) isn't the
            // primary - the exact nodes this list exists to target.
            var hedgeNodes = url.Servers
                .Select(server => $"mongodb://{server.Host}:{server.Port}/{url.DatabaseName}?directConnection=true&readPreference=secondary")
                .Where(directConnectionString => !IsCurrentPrimary(directConnectionString))
                .Select(directConnectionString => (IClickMetaQueryService)new MongoClickMetaQueryService(directConnectionString))
                .ToList();

            return new HedgedClickMetaQueryService(
                fallback, hedgeNodes, TimeSpan.FromMilliseconds(hedgeDelayMs),
                sp.GetRequiredService<ILogger<HedgedClickMetaQueryService>>());
        });

        return builder;
    }

    // One-time, synchronous (this only ever runs once, while this DI factory builds the
    // singleton at startup) `hello` check against a single direct-connected node - the same
    // "ask the real thing, don't trust a static label" principle control-api's own
    // MongoTopologyService already applies via `rs.status()`, just reached through the driver
    // instead of execing mongosh (this runs inside the product backend, which has no docker-exec
    // access). isWritablePrimary is the modern (MongoDB 5.0+) replacement for the legacy
    // `ismaster` field - this stack runs MongoDB 7.
    private static bool IsCurrentPrimary(string directConnectionString)
    {
        try
        {
            var hello = new MongoClient(directConnectionString).GetDatabase("admin")
                .RunCommand<BsonDocument>(new BsonDocument("hello", 1));
            return hello.GetValue("isWritablePrimary", false).ToBoolean();
        }
        catch
        {
            // Unreachable at startup - definitely not a primary worth hedging against either way.
            return false;
        }
    }
}
