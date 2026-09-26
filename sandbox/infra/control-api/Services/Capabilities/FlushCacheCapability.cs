namespace ControlApi.Services.Capabilities;

public sealed class FlushCacheCapability(IDockerService docker) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapPost("/api/containers/redis/flush-cache", async (CancellationToken ct) =>
        {
            var result = await docker.FlushRedisAsync(ct);
            return result is null ? Results.NotFound() : Results.Ok(new { flushed = result });
        });
    }
}
