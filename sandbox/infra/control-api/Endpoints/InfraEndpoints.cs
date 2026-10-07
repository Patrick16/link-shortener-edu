using ControlApi.Services;

namespace ControlApi.Endpoints;

// Read-only views of the infrastructure's live state.
public static class InfraEndpoints
{
    public static IEndpointRouteBuilder MapInfraEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/containers/pgcat/connections", async (IPgcatService pgcat, CancellationToken ct) =>
        {
            var result = await pgcat.GetPgcatConnectionsAsync(ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapGet("/api/containers/postgres/connections", async (IPostgresService postgres, CancellationToken ct) =>
        {
            var result = await postgres.GetPostgresConnectionsAsync(ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        // One-shot fetch for a page's first paint - TopologyPollerService pushes every change after
        // that over the hub ("infraTopologyUpdated"), so the frontend doesn't poll these on its own.
        app.MapGet("/api/containers/redis/topology", async (IRedisInfraService redis, CancellationToken ct) =>
            Results.Ok(await redis.GetRedisTopologyAsync(ct)));

        app.MapGet("/api/containers/mongo/topology", async (IMongoTopologyService mongo, CancellationToken ct) =>
            Results.Ok(await mongo.GetMongoTopologyAsync(ct)));

        app.MapGet("/api/infra/status", (IInfraToggleService infraToggle) => Results.Ok(infraToggle.GetInfraStatus()));

        return app;
    }
}
