using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class RedisSentinelToggleCapability(IInfraToggleService infraToggle, ILogger<RedisSentinelToggleCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapPost("/api/infra/redis-sentinel", async (InfraToggleRequest request, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await infraToggle.SetRedisSentinelEnabledAsync(request.Enabled, ct));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Toggling Redis Sentinel to {Enabled} failed", request.Enabled);
                return Results.Problem(ex.Message);
            }
        });
    }
}
