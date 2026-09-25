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
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IMessageFallbackStore _fallbackStore = fallbackStore;
    private readonly IMessagePublisher _publisher = publisher;
    private readonly ILogger<RabbitMqRetryWorker> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
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
