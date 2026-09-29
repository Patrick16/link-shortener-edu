using Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace WebDefaults;

public static class HealthEndpointsExtensions
{
    // Liveness: the process can respond at all - no dependency checks. Readiness: can it actually
    // serve traffic right now - runs the checks tagged Constants.ReadyHealthCheckTag (registered via
    // Infrastructure.HealthChecksBuilderExtensions). The workers (ShortenerService, TrafficService)
    // have an HTTP listener for these two endpoints and nothing else.
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new() { Predicate = _ => false });
        endpoints.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains(Constants.ReadyHealthCheckTag) });
        return endpoints;
    }
}
