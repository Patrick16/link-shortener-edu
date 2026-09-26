using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

public sealed class NginxToggleCapability(IDockerService docker) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        // "Enabled" is framed the same way for all three - true is the normal/default state, false is
        // the degraded one being demonstrated - even though nginx's own field name (NginxBypassed) is
        // the inverse of that, since bypassing is the interesting state worth naming directly there.
        app.MapPost("/api/infra/nginx", (InfraToggleRequest request) =>
            Results.Ok(docker.SetNginxBypass(!request.Enabled)));
    }
}
