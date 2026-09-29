using Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure;

// One place for the names/tags of the dependency checks, so every service reports them the same way
// on /health/ready (see WebDefaults.HealthEndpointsExtensions).
public static class HealthChecksBuilderExtensions
{
    private static readonly string[] ReadyTags = [Constants.ReadyHealthCheckTag];

    // For a context registered with AddPostgresDbContextPool.
    public static IHealthChecksBuilder AddPostgresHealthCheck<TContext>(this IHealthChecksBuilder builder)
        where TContext : DbContext
        => builder.AddCheck<DbContextHealthCheck<TContext>>("database", tags: ReadyTags);

    // For a context registered with AddPostgresDbContextFactory.
    public static IHealthChecksBuilder AddPostgresFactoryHealthCheck<TContext>(this IHealthChecksBuilder builder)
        where TContext : DbContext
        => builder.AddCheck<DbContextFactoryHealthCheck<TContext>>("database", tags: ReadyTags);

    public static IHealthChecksBuilder AddRabbitMqHealthCheck(this IHealthChecksBuilder builder)
        => builder.AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ReadyTags);

    public static IHealthChecksBuilder AddMongoHealthCheck(this IHealthChecksBuilder builder)
        => builder.AddCheck<MongoHealthCheck>("mongo", tags: ReadyTags);
}
