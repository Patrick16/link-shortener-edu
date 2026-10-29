using Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure;

// Mirrors ClickHouseExtensions's writer/reader split for clicks_meta_db - write side (TrafficService,
// the sole owner) and read side (ReportingApi) each get their own DI registration, so neither
// service can accidentally depend on the half of the capability it doesn't use.
public static class MongoExtensions
{
    // Write side (TrafficService) - the only service that ever upserts into clicks_meta_db.
    public static IHostApplicationBuilder AddMongoClickMetaWriter(this IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString(Constants.MongoDbConnectionString);
        builder.Services.AddSingleton<IClickMetaStore>(_ => new MongoClickMetaStore(connectionString!));
        return builder;
    }

    // Read side (ReportingApi) - the only service that ever queries clicks_meta_db.
    public static IHostApplicationBuilder AddMongoClickMetaReader(this IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString(Constants.MongoDbConnectionString);
        builder.Services.AddSingleton<IClickMetaQueryService>(_ => new MongoClickMetaQueryService(connectionString!));
        return builder;
    }
}
