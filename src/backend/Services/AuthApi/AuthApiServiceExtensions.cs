using AuthApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace AuthApi;

// AuthApi-only wiring - the parts of Program.cs that no other service shares.
public static class AuthApiServiceExtensions
{
    // Shared with Program.cs (UseRateLimiter doesn't need the name, but MapControllers'
    // [EnableRateLimiting] attribute on AuthController does) and AuthController itself.
    public const string AuthRateLimitPolicy = "auth-per-ip";

    public static WebApplicationBuilder AddAuthServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
        // Scoped, not Singleton - it depends on the pooled (scoped) DatabaseContext.
        builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        builder.Services.AddHostedService<RefreshTokenCleanupWorker>();
        return builder;
    }

    // Guards register/login/refresh/logout (the whole of AuthController - see its [EnableRateLimiting]
    // attribute) against a single client hammering them; /health/live and /health/ready are mapped
    // outside the controller (see Program.cs) and never go through this policy, so orchestrator/nginx
    // health probes can't trip it.
    //
    // Deliberately NOT services.AddRateLimiter(o => o.AddFixedWindowLimiter(AuthRateLimitPolicy, ...))
    // - that overload (the one a reviewer's suggested snippet used) builds ONE shared limiter for every
    // caller combined, not a separate bucket per client. Verified live with two simulated RemoteIpAddress
    // values against both shapes: AddFixedWindowLimiter let the second "IP" get rejected by the first
    // one's quota, while the AddPolicy+RateLimitPartition shape below gave each its own window - for a
    // public deploy, the shared-limiter shape would mean one abusive caller locks out every other user
    // from registering/logging in, the opposite of what this feature is for.
    public static WebApplicationBuilder AddAuthRateLimiting(this WebApplicationBuilder builder)
    {
        // Read directly from configuration (not IOptions<T>) - this is consumed once, synchronously,
        // while building the limiter policy below, so there's nothing for IOptions<T>'s change-
        // notification machinery to do here.
        var rateLimiting = builder.Configuration.GetSection("RateLimiting").Get<RateLimitingOptions>() ?? new();

        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Without this, a rejection still comes back as RFC 7807 JSON (RateLimiterMiddleware
            // writes one itself once AddProblemDetails() is registered - see
            // ApiExceptionHandlingExtensions) but with only {status, traceId} - no Title/Type, unlike
            // every other error path in this API (e.g. a bare 404 gets Title "Not Found" filled in by
            // UseStatusCodePages's own default writer). Found by live-testing a real rejection against
            // the running container, not by reading the middleware's source. Explicit here so a 429
            // reads the same as everything else.
            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var problemDetailsService = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too many requests.",
                        Detail = "Too many requests from this client in a short time - wait and try again.",
                        Type = "https://tools.ietf.org/html/rfc6585#section-4",
                    },
                });
            };
            limiter.AddPolicy(AuthRateLimitPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    // RemoteIpAddress is null only for non-socket transports (e.g. in-process test
                    // servers) - AuthApi is never reached that way outside tests, where this doesn't
                    // run; the "unknown" bucket exists so a null here fails safe (shared, generous
                    // quota) instead of throwing.
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = rateLimiting.PermitLimit,
                        Window = TimeSpan.FromSeconds(rateLimiting.WindowSeconds),
                    }));
        });

        return builder;
    }
}
