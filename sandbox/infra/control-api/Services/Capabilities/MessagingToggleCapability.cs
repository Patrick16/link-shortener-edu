using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class MessagingToggleCapability(IInfraToggleService infraToggle, ILogger<MessagingToggleCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapPost("/api/infra/messaging-mode", async (MessagingModeRequest request, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await infraToggle.SetMessagingModeAsync(request.Mode, ct));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Switching messaging mode to {Mode} failed", request.Mode);
                return Results.Problem(ex.Message);
            }
        });
    }
}
