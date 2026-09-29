using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Infrastructure;

// For services with a directly-injectable DbContext (AddDbContextPool) — AuthApi, LinkApi,
// RedirectApi. Resolved from the request-scoped DI container the health check middleware creates,
// so this reuses the pooled context the same way a controller would.
public sealed class DbContextHealthCheck<TContext>(TContext context) : IHealthCheck
    where TContext : DbContext
{
    // CanConnectAsync goes through the same pgcat pool real traffic uses, so under pool exhaustion it
    // just queues like any other request - observed hanging 20+s during a 200-VU run. Bounded well
    // under docker-compose's healthcheck `timeout: 5s` so a saturated pool fails the probe fast
    // instead of silently eating Docker's own timeout budget, and so this check stops holding a
    // queued pool slot for as long as a real request would. See sandbox/docs/pgcat-pool-sizing.md.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext healthCheckContext,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Timeout);

        try
        {
            return await context.Database.CanConnectAsync(timeoutCts.Token)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database is not reachable.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"Database did not respond within {Timeout.TotalSeconds}s (connection pool likely exhausted).");
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
    // See DbContextHealthCheck<TContext>'s identical field for why this exists.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext healthCheckContext,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Timeout);

        try
        {
            await using var context = await factory.CreateDbContextAsync(timeoutCts.Token);
            return await context.Database.CanConnectAsync(timeoutCts.Token)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database is not reachable.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"Database did not respond within {Timeout.TotalSeconds}s (connection pool likely exhausted).");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database is not reachable.", ex);
        }
    }
}
