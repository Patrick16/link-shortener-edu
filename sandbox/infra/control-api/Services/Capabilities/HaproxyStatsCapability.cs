namespace ControlApi.Services.Capabilities;

public sealed class HaproxyStatsCapability(IHaproxyService haproxy, ILogger<HaproxyStatsCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/infra/haproxy-stats", async (CancellationToken ct) =>
        {
            try
            {
                var result = await haproxy.GetHaproxyStatsAsync(ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reading haproxy stats failed");
                return Results.Problem(ex.Message);
            }
        });
    }
}
