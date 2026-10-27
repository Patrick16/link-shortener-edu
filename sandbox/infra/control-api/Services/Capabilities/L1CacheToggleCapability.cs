using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

// Own GET/POST pair, not folded into InfraStatus/TopologyPanel - see InfraStatus's own comment on
// why. Response shape mirrors RabbitMqPrefetchCapability/MongoReadPreferenceCapability (a small
// anonymous object, not the whole InfraStatus), since this isn't one of that record's fields.
public sealed class L1CacheToggleCapability(IInfraToggleService infraToggle, ILogger<L1CacheToggleCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/infra/l1-cache", () => Results.Ok(new { enabled = infraToggle.GetL1CacheEnabled() }));

        app.MapPost("/api/infra/l1-cache", async (InfraToggleRequest request, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(new { enabled = await infraToggle.SetL1CacheEnabledAsync(request.Enabled, ct) });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Toggling L1 cache to {Enabled} failed", request.Enabled);
                return Results.Problem(ex.Message);
            }
        });
    }
}
