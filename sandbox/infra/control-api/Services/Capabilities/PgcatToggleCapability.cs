using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class PgcatToggleCapability(IInfraToggleService infraToggle, ILogger<PgcatToggleCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapPost("/api/infra/pgcat", async (InfraToggleRequest request, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await infraToggle.SetPgcatEnabledAsync(request.Enabled, ct));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Toggling pgcat to {Enabled} failed", request.Enabled);
                return Results.Problem(ex.Message);
            }
        });
    }
}
