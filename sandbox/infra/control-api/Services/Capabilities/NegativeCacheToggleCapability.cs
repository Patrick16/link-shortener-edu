using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

// Own GET/POST pair, same shape as L1CacheToggleCapability - see IInfraToggleService's own
// comment on GetNegativeCacheEnabled for why this isn't an InfraStatus/Topology toggle either.
public sealed class NegativeCacheToggleCapability(IInfraToggleService infraToggle, ILogger<NegativeCacheToggleCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/infra/negative-cache", () => Results.Ok(new { enabled = infraToggle.GetNegativeCacheEnabled() }));

        app.MapPost("/api/infra/negative-cache", async (InfraToggleRequest request, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(new { enabled = await infraToggle.SetNegativeCacheEnabledAsync(request.Enabled, ct) });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Toggling negative cache to {Enabled} failed", request.Enabled);
                return Results.Problem(ex.Message);
            }
        });
    }
}
