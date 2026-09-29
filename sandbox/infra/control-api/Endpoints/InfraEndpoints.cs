using ControlApi.Services;

namespace ControlApi.Endpoints;

// Read-only views of the infrastructure's live state.
public static class InfraEndpoints
{
    public static IEndpointRouteBuilder MapInfraEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/containers/pgcat/connections", async (IDockerService docker, CancellationToken ct) =>
        {
            var result = await docker.GetPgcatConnectionsAsync(ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapGet("/api/containers/postgres/connections", async (IDockerService docker, CancellationToken ct) =>
        {
            var result = await docker.GetPostgresConnectionsAsync(ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        // One-shot fetch for a page's first paint - TopologyPollerService pushes every change after
        // that over the hub ("infraTopologyUpdated"), so the frontend doesn't poll these on its own.
        app.MapGet("/api/containers/redis/topology", async (IDockerService docker, CancellationToken ct) =>
            Results.Ok(await docker.GetRedisTopologyAsync(ct)));

        app.MapGet("/api/containers/mongo/topology", async (IDockerService docker, CancellationToken ct) =>
            Results.Ok(await docker.GetMongoTopologyAsync(ct)));

        app.MapGet("/api/infra/status", (IDockerService docker) => Results.Ok(docker.GetInfraStatus()));

        return app;
    }
}
