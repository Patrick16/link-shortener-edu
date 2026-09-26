using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class ReplicationLagCapability(IDockerService docker) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/containers/{serviceId}/replication-lag", async (string serviceId, CancellationToken ct) =>
        {
            var result = await docker.GetReplicationLagAsync(serviceId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/containers/{serviceId}/replication-lag", async (string serviceId, ReplicationLag request, CancellationToken ct) =>
        {
            if (request.DelayMs is < 0 or > 60_000)
            {
                return Results.BadRequest(new { error = "delayMs must be between 0 and 60000" });
            }

            var result = await docker.SetReplicationLagAsync(serviceId, request.DelayMs, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
    }
}
