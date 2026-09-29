using ControlApi.Services;
using ControlApi.Services.Capabilities;

namespace ControlApi.Extensions;

public static class CapabilityEndpointExtensions
{
    // Component-specific infra endpoints (pgcat pool, Sentinel config, etc.) are registered per
    // capability class rather than mapped unconditionally - see CapabilityFactory for how the set
    // actually mapped is driven by which capability strings architecture.json's nodes reference.
    // A missing/unreadable architecture file only costs those endpoints, never the whole app.
    public static WebApplication MapCapabilityEndpoints(this WebApplication app)
    {
        var architectureFile = app.Configuration["Architecture:File"]
            ?? "/workspace/frontend/architecture-map/src/data/architecture.json";
        try
        {
            var capabilities = new CapabilityFactory().BuildFromArchitectureFile(
                architectureFile,
                app.Services.GetRequiredService<IDockerService>(),
                app.Services.GetRequiredService<ILoggerFactory>());
            foreach (var capability in capabilities)
            {
                capability.MapEndpoints(app);
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Failed to load capabilities from architecture file {Path} - component-specific infra endpoints will be unavailable", architectureFile);
        }

        return app;
    }
}
