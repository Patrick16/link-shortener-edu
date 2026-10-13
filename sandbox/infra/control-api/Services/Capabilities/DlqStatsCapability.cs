using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class DlqStatsCapability(IRabbitMqService rabbitMq, ILogger<DlqStatsCapability> logger) : IComponentCapability
{
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
    }
}
