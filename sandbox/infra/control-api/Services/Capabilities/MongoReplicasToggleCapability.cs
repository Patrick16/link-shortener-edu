using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class MongoReplicasToggleCapability(IInfraToggleService infraToggle, ILogger<MongoReplicasToggleCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapPost("/api/infra/mongo-replicas", async (InfraToggleRequest request, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await infraToggle.SetMongoReplicasEnabledAsync(request.Enabled, ct));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Toggling Mongo replicas to {Enabled} failed", request.Enabled);
                return Results.Problem(ex.Message);
            }
        });
    }
}
