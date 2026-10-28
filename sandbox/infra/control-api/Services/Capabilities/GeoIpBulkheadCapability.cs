using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class GeoIpBulkheadCapability(IInfraToggleService infraToggle, ILogger<GeoIpBulkheadCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/infra/geoip-bulkhead", () => Results.Ok(new { limit = infraToggle.GetGeoIpBulkheadLimit() }));

        app.MapPost("/api/infra/geoip-bulkhead", async (GeoIpBulkheadRequest request, CancellationToken ct) =>
        {
            if (request.Limit is < 1 or > 1000)
            {
                return Results.BadRequest(new { error = "limit must be between 1 and 1000" });
            }

            try
            {
                return Results.Ok(new { limit = await infraToggle.SetGeoIpBulkheadLimitAsync(request.Limit, ct) });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Setting geo-IP bulkhead limit failed");
                return Results.Problem(ex.Message);
            }
        });
    }
}
