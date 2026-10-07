using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class RabbitMqPrefetchCapability(IInfraToggleService infraToggle, ILogger<RabbitMqPrefetchCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/infra/rabbitmq-prefetch", () => Results.Ok(new { prefetchCount = infraToggle.GetRabbitMqPrefetch() }));

        app.MapPost("/api/infra/rabbitmq-prefetch", async (RabbitMqPrefetchRequest request, CancellationToken ct) =>
        {
            if (request.PrefetchCount is < 1 or > 1000)
            {
                return Results.BadRequest(new { error = "prefetchCount must be between 1 and 1000" });
            }

            try
            {
                return Results.Ok(new { prefetchCount = await infraToggle.SetRabbitMqPrefetchAsync(request.PrefetchCount, ct) });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Setting RabbitMQ prefetch failed");
                return Results.Problem(ex.Message);
            }
        });
    }
}
