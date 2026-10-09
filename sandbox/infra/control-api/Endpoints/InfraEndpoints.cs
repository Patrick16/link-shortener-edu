using ControlApi.Services;

namespace ControlApi.Endpoints;

// Read-only views of the infrastructure's live state.
public static class InfraEndpoints
{
    public static IEndpointRouteBuilder MapInfraEndpoints(this IEndpointRouteBuilder app)
    {
        // Parameterized (not a fixed "pgcat" literal, unlike postgres/connections below) - there are
        // 3 pgcat instances now (Pooler Scaling, 2026-10-09), each with its own pools, same shape as
        // replication-lag's own {serviceId} route for postgres-replica1/2.
        app.MapGet("/api/containers/{serviceId}/pgcat-connections", async (string serviceId, IPgcatService pgcat, CancellationToken ct) =>
        {
            var result = await pgcat.GetPgcatConnectionsAsync(serviceId, ct);
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
