namespace ControlApi.Services.Capabilities;

public sealed class LinkExpiryLeaderCapability(IRedisInfraService redis, ILogger<LinkExpiryLeaderCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/infra/link-expiry-leader", async (CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await redis.GetLinkExpiryLeaderAsync(ct));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reading link-expiry leader status failed");
                return Results.Problem(ex.Message);
            }
        });
    }
}
