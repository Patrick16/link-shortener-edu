using ControlApi.Models;
using ControlApi.Services;

namespace ControlApi.Endpoints;

public static class TrafficEndpoints
{
    public static IEndpointRouteBuilder MapTrafficEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/traffic", (TrafficRequest request, ITrafficService traffic, TrafficRunCoordinator coordinator) =>
        {
            var error = TrafficRequestValidator.Validate(request, traffic);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            return coordinator.TryStart(request)
                ? Results.Accepted()
                : Results.Conflict(new { error = "a traffic run is already in progress" });
        });

        app.MapPost("/api/traffic/cancel", (TrafficRunCoordinator coordinator) =>
            coordinator.TryCancel()
                ? Results.Accepted()
                : Results.Conflict(new { error = "no traffic run is currently active" }));

        return app;
    }
}
