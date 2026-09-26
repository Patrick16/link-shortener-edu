using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class SentinelConfigCapability(IDockerService docker) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/infra/sentinel", async (CancellationToken ct) =>
        {
            var result = await docker.GetSentinelConfigAsync(ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/infra/sentinel", async (SentinelConfig request, CancellationToken ct) =>
        {
            if (request.DownAfterMs is < 100 or > 60_000)
            {
                return Results.BadRequest(new { error = "downAfterMs must be between 100 and 60000" });
            }

            if (request.Quorum is < 1 or > 3)
            {
                return Results.BadRequest(new { error = "quorum must be between 1 and 3" });
            }

            if (request.FailoverTimeoutMs is < 1000 or > 300_000)
            {
                return Results.BadRequest(new { error = "failoverTimeoutMs must be between 1000 and 300000" });
            }

            return Results.Ok(await docker.SetSentinelConfigAsync(request, ct));
        });
    }
}
