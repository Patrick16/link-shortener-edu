using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class PostgresReplicasToggleCapability(IInfraToggleService infraToggle, ILogger<PostgresReplicasToggleCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapPost("/api/infra/postgres-replicas", async (InfraToggleRequest request, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await infraToggle.SetPostgresReplicasEnabledAsync(request.Enabled, ct));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Toggling postgres replicas to {Enabled} failed", request.Enabled);
                return Results.Problem(ex.Message);
            }
        });
    }
}
