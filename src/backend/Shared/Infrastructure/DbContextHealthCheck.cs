using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Infrastructure;

// For services with a directly-injectable DbContext (AddDbContextPool) — AuthApi, LinkApi,
// RedirectApi. Resolved from the request-scoped DI container the health check middleware creates,
// so this reuses the pooled context the same way a controller would.
public sealed class DbContextHealthCheck<TContext>(TContext context) : IHealthCheck
    where TContext : DbContext
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext healthCheckContext,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await context.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database is not reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database is not reachable.", ex);
        }
    }
}

// For services that only expose an IDbContextFactory (AddPooledDbContextFactory) — ShortenerService,
// TrafficService. Their DatabaseContext isn't registered directly in DI, so it's created and
// disposed here instead of injected.
public sealed class DbContextFactoryHealthCheck<TContext>(IDbContextFactory<TContext> factory) : IHealthCheck
    where TContext : DbContext
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext healthCheckContext,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            return await context.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database is not reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database is not reachable.", ex);
        }
    }
}
