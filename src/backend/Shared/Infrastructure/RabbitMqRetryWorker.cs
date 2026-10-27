using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure;

// Periodically retries messages that were saved to the SQLite fallback because RabbitMQ was
// unavailable. Generic — used by any service that publishes (LinkApi, RedirectApi), not tied to
// a specific event type.
public class RabbitMqRetryWorker(
    IMessageFallbackStore fallbackStore,
    IMessagePublisher publisher,
    ILogger<RabbitMqRetryWorker> logger) : BackgroundService
{
    // Full jitter (random(0, base), not base +-10%) - every LinkApi/RedirectApi replica runs its
    // own instance of this worker against its own local SQLite fallback file, so a +-10% jitter
    // would still leave every replica's tick within ~3s of every other's if they started around the
    // same deploy moment - nowhere near enough separation to break a retry storm against RabbitMQ
    // once it actually comes back after an outage (see this item's own PLAN.md framing). Re-rolled
    // every tick via NextPollDelay (not generated once at startup) so replicas that happened to
    // start within the same window keep drifting apart over time instead of staying correlated.
    // Trades a lower average interval (expected 15s, not 30s - full jitter's accepted cost) for
    // that decorrelation.
    private const double PollIntervalSeconds = 30;

    private readonly IMessageFallbackStore _fallbackStore = fallbackStore;
    private readonly IMessagePublisher _publisher = publisher;
    private readonly ILogger<RabbitMqRetryWorker> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(NextPollDelay(), stoppingToken);

            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A transient failure here (e.g. the SQLite fallback file briefly locked by another
                // replica) must not crash the whole host - .NET's default BackgroundService
                // exception behavior would otherwise stop the entire API on the next unhandled
                // exception from this loop.
                _logger.LogWarning(ex, "Fallback retry tick failed - will retry next tick");
            }
        }
    }

    // Random.Shared, not a seeded/injected instance - thread-safe and process-wide (.NET 6+); a
    // fixed seed would defeat the entire point of decorrelating replicas from each other.
    internal static TimeSpan NextPollDelay() =>
        TimeSpan.FromSeconds(Random.Shared.NextDouble() * PollIntervalSeconds);

    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var pending = await _fallbackStore.GetPendingAsync(cancellationToken);
        foreach (var message in pending)
        {
            var republished = await _publisher.TryRepublishAsync(message, cancellationToken);
            if (republished)
            {
                await _fallbackStore.DeleteAsync(message.MessageId, cancellationToken);
            }
            else
            {
                _logger.LogWarning(
                    "Retry failed for fallback message {MessageId} (topic {Topic})",
                    message.MessageId,
                    message.Topic);
            }
        }
    }
}
