using Microsoft.AspNetCore.Builder;
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

    // AddProblemDetails() alone only formats responses that already call IProblemDetailsService -
    // an unhandled exception caught by GlobalExceptionHandler, or an explicit Problem()/
    // ValidationProblem() result. A bare `return NotFound();` is an empty-body NotFoundResult; it
    // needs UseStatusCodePages() too, and that middleware's own default writer is plain text ("Status
    // Code: 404; Not Found"), not ProblemDetails - the delegate overload below is what actually
    // routes it through the same IProblemDetailsService as every other error path, so a bare
    // NotFound()/BadRequest()/etc. gets the identical {type,title,status,detail,traceId} JSON shape
    // as a caught exception or an explicit Problem() call, matching what architecture.md/
    // 01-minimal.md document as a uniform contract across all three APIs.
    public static IApplicationBuilder UseApiExceptionHandling(this IApplicationBuilder app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages(async statusCodeContext =>
        {
            var problemDetailsService = statusCodeContext.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
            await problemDetailsService.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = statusCodeContext.HttpContext,
            });
        });

        return app;
    }
}
