using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class PgcatPoolCapability(IDockerService docker, ILogger<PgcatPoolCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/infra/pgcat-pool", () => Results.Ok(docker.GetPgcatPoolSettings()));

        app.MapPost("/api/infra/pgcat-pool", async (PgcatPoolSettings request, CancellationToken ct) =>
        {
            if (request.PoolMode is not ("transaction" or "session"))
            {
                return Results.BadRequest(new { error = "poolMode must be 'transaction' or 'session'" });
            }

            if (request.PoolSize is < 1 or > 200)
            {
                return Results.BadRequest(new { error = "poolSize must be between 1 and 200" });
            }

            try
            {
                return Results.Ok(await docker.SetPgcatPoolSettingsAsync(request, ct));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Setting pgcat pool settings failed");
                return Results.Problem(ex.Message);
            }
        });
    }
}
