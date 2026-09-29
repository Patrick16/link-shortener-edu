using Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure;

// Every service owns its own DatabaseContext type but connects to Postgres the same way - through
// PgCat with a small retry policy - so the registration lives here, generic over the context.
public static class PostgresExtensions
{
    // Directly-injectable, request-scoped context (AuthApi, LinkApi, RedirectApi).
    public static IHostApplicationBuilder AddPostgresDbContextPool<TContext>(this IHostApplicationBuilder builder)
        where TContext : DbContext
    {
        var connectionString = builder.Configuration.GetConnectionString(Constants.PostgresConnectionString);
        builder.Services.AddDbContextPool<TContext>(
            options => options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(4L), null)));
        return builder;
    }

    // Factory-based context for services with no request scope (ShortenerService, TrafficService) -
    // their hosted-service consumers create a short-lived context per message batch.
    public static IHostApplicationBuilder AddPostgresDbContextFactory<TContext>(this IHostApplicationBuilder builder)
        where TContext : DbContext
    {
        var connectionString = builder.Configuration.GetConnectionString(Constants.PostgresConnectionString);
        builder.Services.AddPooledDbContextFactory<TContext>(
            options => options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(4L), null)));
        return builder;
    }

    // Applies pending EF Core migrations. Connects directly to the primary, bypassing PgCat, for this
    // call specifically (see Constants.PostgresPrimaryConnectionString); falls back to the regular
    // connection string when no separate primary one is configured. Call after Build(), before Run().
    public static async Task MigratePostgresAsync<TContext>(this IHost host, CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString(Constants.PostgresPrimaryConnectionString)
            ?? configuration.GetConnectionString(Constants.PostgresConnectionString);

        var options = new DbContextOptionsBuilder<TContext>().UseNpgsql(connectionString).Options;
        await using var context = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        await context.Database.MigrateAsync(cancellationToken);
    }
}
