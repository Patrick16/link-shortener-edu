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
            var pending = await _fallbackStore.GetPendingAsync(stoppingToken);
            foreach (var message in pending)
            {
                var republished = await _publisher.TryRepublishAsync(message, stoppingToken);
                if (republished)
                {
                    await _fallbackStore.DeleteAsync(message.MessageId, stoppingToken);
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
}
