using ControlApi.Models;

namespace ControlApi.Services.Capabilities;

// Own GET/POST pair, same shape as L1CacheToggleCapability/NegativeCacheToggleCapability - not
// an InfraStatus/Topology toggle (see IInfraToggleService's own comment on
// GetCircuitBreakersEnabled). Rendered on all 6 circuit-breaker-registering nodes (see
// architecture.json), not just one or two - toggling from any of them affects all 6 together.
public sealed class CircuitBreakerToggleCapability(IInfraToggleService infraToggle, ILogger<CircuitBreakerToggleCapability> logger) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/infra/circuit-breakers", () => Results.Ok(new { enabled = infraToggle.GetCircuitBreakersEnabled() }));

        app.MapPost("/api/infra/circuit-breakers", async (InfraToggleRequest request, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(new { enabled = await infraToggle.SetCircuitBreakersEnabledAsync(request.Enabled, ct) });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Toggling circuit breakers to {Enabled} failed", request.Enabled);
                return Results.Problem(ex.Message);
            }
        });
    }
}
