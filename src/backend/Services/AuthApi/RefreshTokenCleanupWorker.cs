using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AuthApi;

// Every login/refresh/rotation inserts a refresh_tokens row; rotation/logout only ever set
// RevokedAt, nothing ever deleted one - the table grows without bound as users log in and out over
// the deployment's lifetime, even though revoked/expired rows are never queried again (lookups are
// by TokenHash, and RotateAsync already treats an expired row as unusable). Deleting once a token
// is past its own ExpiresAt (whether it was revoked or just outlived its 14-day window) bounds the
// table to roughly (active users x tokens issued within that window) instead of every token ever
// issued.
public class RefreshTokenCleanupWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<RefreshTokenCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<RefreshTokenCleanupWorker> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CleanupOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A transient failure here (e.g. Postgres briefly unreachable) must not crash the
                // whole host - .NET's default BackgroundService exception behavior would otherwise
                // stop the entire API on the next unhandled exception from this loop.
                _logger.LogWarning(ex, "Refresh token cleanup tick failed - will retry next tick");
            }
        }
    }

    internal async Task<int> CleanupOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        var deleted = await context.RefreshTokens
            .Where(x => x.ExpiresAt <= DateTime.UtcNow)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            _logger.LogInformation("Deleted {Count} expired refresh token(s)", deleted);
        }

        return deleted;
    }
}
