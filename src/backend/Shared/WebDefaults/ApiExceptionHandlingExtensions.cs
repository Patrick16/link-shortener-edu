using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace WebDefaults;

public static class ApiExceptionHandlingExtensions
{
    // Wires GlobalExceptionHandler as the catch-all for unhandled exceptions, and turns on
    // ProblemDetails for every client/server error response — including the bare NotFound()/
    // Conflict()/Unauthorized() results [ApiController] actions already return, so the whole API
    // answers in one consistent JSON shape instead of a mix of plain text and JSON.
    public static IServiceCollection AddApiExceptionHandling(this IServiceCollection services)
    {
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
            };
        });

        return services;
    }
}
