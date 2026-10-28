using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class DlqStatsCapability(IRabbitMqService rabbitMq, ILogger<DlqStatsCapability> logger) : IComponentCapability
{
    // Replay is an operator-triggered, one-shot action (a button click), not a background loop -
    // there's no retry schedule to jitter. The real risk this item's own pitfall note (PLAN.md
    // item 13) warns about is a human mashing the button (or a script calling this repeatedly)
    // against a consumer that's still broken, turning "replay once" into the same hot-retry-loop
    // shape a backoff would normally guard against. A short cooldown between calls is enough to
    // prevent that without needing anything stateful across restarts (control-api itself isn't
    // scaled - see ContainerLifecycleService's own ScalableServices list - so in-process state is
    // sufficient here, unlike the distributed rate limiter the product backend needed).
    private static readonly TimeSpan ReplayCooldown = TimeSpan.FromSeconds(5);
    private readonly Lock _replayLock = new();
    private DateTime? _lastReplayUtc;

    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/infra/dlq-stats", async (CancellationToken ct) =>
        {
            try
            {
                var result = await rabbitMq.GetDeadLetterQueueStatsAsync(ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reading dead-letter queue stats failed");
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/infra/dlq-clear", async (CancellationToken ct) =>
        {
            try
            {
                var result = await rabbitMq.ClearDeadLetterQueuesAsync(ct);
                return result is null ? Results.NotFound() : Results.Ok(new { cleared = result.Value });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Clearing dead-letter queues failed");
                return Results.Problem(ex.Message);
            }
        });

        // GET, not POST - this only peeks (ack_requeue_true, see RabbitMqService), never removes
        // anything, so it's safe to call as often as the panel wants without any cooldown.
        app.MapGet("/api/infra/dlq/{queue}/sample", async (string queue, int? count, CancellationToken ct) =>
        {
            var sampleSize = Math.Clamp(count ?? 5, 1, 20);
            try
            {
                var result = await rabbitMq.SampleDeadLetterQueueAsync(queue, sampleSize, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sampling dead-letter queue {Queue} failed", queue);
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/infra/dlq/replay", async (DlqReplayRequest request, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.QueueName) || request.Count < 1)
            {
                return Results.BadRequest(new { error = "queueName is required and count must be at least 1" });
            }

            lock (_replayLock)
            {
                var now = DateTime.UtcNow;
                if (_lastReplayUtc is { } last && now - last < ReplayCooldown)
                {
                    return Results.Problem(
                        $"Replay was triggered less than {ReplayCooldown.TotalSeconds:0}s ago - wait before retrying, " +
                        "so a still-broken consumer can't be hammered by repeated replay attempts.",
                        statusCode: StatusCodes.Status429TooManyRequests);
                }

                _lastReplayUtc = now;
            }

            try
            {
                var result = await rabbitMq.ReplayDeadLetterQueueAsync(request.QueueName, request.Count, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Replaying dead-letter queue {Queue} failed", request.QueueName);
                return Results.Problem(ex.Message);
            }
        });
    }
}
