using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class MongoReadPreferenceCapability(IDockerService docker, ILogger<MongoReadPreferenceCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/infra/mongo-read-preference", () => Results.Ok(new { preference = docker.GetMongoReadPreference() }));

        app.MapPost("/api/infra/mongo-read-preference", async (MongoReadPreferenceRequest request, CancellationToken ct) =>
        {
            if (request.Preference is not ("primary" or "secondaryPreferred"))
            {
                return Results.BadRequest(new { error = "preference must be 'primary' or 'secondaryPreferred'" });
            }

            try
            {
                return Results.Ok(new { preference = await docker.SetMongoReadPreferenceAsync(request.Preference, ct) });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Setting Mongo read preference failed");
                return Results.Problem(ex.Message);
            }
        });
    }
}
