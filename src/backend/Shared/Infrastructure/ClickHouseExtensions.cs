using Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure;

// No EF Core provider exists for ClickHouse, so unlike PostgresExtensions there's no DbContext/
// migration pair here - the `clicks` table is created once by a SQL file ClickHouse's own official
// image runs from /docker-entrypoint-initdb.d/ on first boot (see sandbox/infra/clickhouse/init.sql),
// the same mechanism Postgres already uses for init-databases.sql. Nothing in the app ever creates
// or alters this schema.
public static class ClickHouseExtensions
{
    // Named client resolved via IHttpClientFactory - both stores construct a fresh ClickHouseClient/
    // ClickHouseConnection wrapper per call (cheap, holds no socket itself), but each is handed the
    // SAME pooled HttpMessageHandler through this factory, so the actual TCP connections/keep-alive
    // ARE reused instead of torn down and reopened on every insert/query (found during review -
    // the original version called `new ClickHouseClient(connectionString)` per call with no factory,
    // which builds its own one-off HttpClient every time).
    private const string HttpClientName = "ClickHouse";

    // Write side (ReportingService) - the only service that ever inserts into ClickHouse.
    public static IHostApplicationBuilder AddClickHouseWriter(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHttpClient(HttpClientName);
        var connectionString = builder.Configuration.GetConnectionString(Constants.ClickHouseConnectionString);
        builder.Services.AddSingleton<IClickFactStore>(sp => new ClickHouseClickFactStore(
            connectionString!, sp.GetRequiredService<IHttpClientFactory>(), HttpClientName));
        return builder;
    }

    // Read side (ReportingApi) - the only service that ever queries ClickHouse.
    public static IHostApplicationBuilder AddClickHouseReader(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHttpClient(HttpClientName);
        var connectionString = builder.Configuration.GetConnectionString(Constants.ClickHouseConnectionString);
        builder.Services.AddSingleton<IClickFactQueryService>(sp => new ClickHouseClickFactQueryService(
            connectionString!, sp.GetRequiredService<IHttpClientFactory>(), HttpClientName));
        return builder;
    }
}
