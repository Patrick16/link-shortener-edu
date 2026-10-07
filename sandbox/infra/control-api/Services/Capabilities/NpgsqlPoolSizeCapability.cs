using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class NpgsqlPoolSizeCapability(IInfraToggleService infraToggle, ILogger<NpgsqlPoolSizeCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/infra/npgsql-pool-size", () => Results.Ok(new { poolSize = infraToggle.GetNpgsqlPoolSize() }));

        app.MapPost("/api/infra/npgsql-pool-size", async (NpgsqlPoolSizeRequest request, CancellationToken ct) =>
        {
            if (request.PoolSize is < 1 or > 500)
            {
                return Results.BadRequest(new { error = "poolSize must be between 1 and 500" });
            }

            try
            {
                return Results.Ok(new { poolSize = await infraToggle.SetNpgsqlPoolSizeAsync(request.PoolSize, ct) });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Setting Npgsql pool size failed");
                return Results.Problem(ex.Message);
            }
        });
    }
}
