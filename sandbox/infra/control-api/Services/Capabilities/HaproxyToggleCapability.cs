using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class HaproxyToggleCapability(IInfraToggleService infraToggle, ILogger<HaproxyToggleCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapPost("/api/infra/haproxy", async (InfraToggleRequest request, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await infraToggle.SetHaproxyEnabledAsync(request.Enabled, ct));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Toggling haproxy to {Enabled} failed", request.Enabled);
                return Results.Problem(ex.Message);
            }
        });
    }
}
