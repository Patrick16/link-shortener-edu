using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class MongoHedgeDelayCapability(IInfraToggleService infraToggle, ILogger<MongoHedgeDelayCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/infra/mongo-hedge-delay", () => Results.Ok(new { delayMs = infraToggle.GetMongoHedgeDelayMs() }));

        app.MapPost("/api/infra/mongo-hedge-delay", async (MongoHedgeDelayRequest request, CancellationToken ct) =>
        {
            if (request.DelayMs is < 0 or > 5000)
            {
                return Results.BadRequest(new { error = "delayMs must be between 0 and 5000" });
            }

            try
            {
                return Results.Ok(new { delayMs = await infraToggle.SetMongoHedgeDelayMsAsync(request.DelayMs, ct) });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Setting Mongo hedge delay failed");
                return Results.Problem(ex.Message);
            }
        });
    }
}
