using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class DlqStatsCapability(IDockerService docker, ILogger<DlqStatsCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/infra/dlq-stats", async (CancellationToken ct) =>
        {
            try
            {
                var result = await docker.GetDeadLetterQueueStatsAsync(ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reading dead-letter queue stats failed");
                return Results.Problem(ex.Message);
            }
        });
    }
}
