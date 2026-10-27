using Infrastructure;
using WebDefaults;

namespace AuthApi;

// AuthApi-only wiring - the parts of Program.cs that no other service shares.
public static class AuthApiServiceExtensions
{
    public static WebApplicationBuilder AddAuthServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
        // Scoped, not Singleton - it depends on the pooled (scoped) DatabaseContext.
        builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        builder.Services.AddHostedService<RefreshTokenCleanupWorker>();
        return builder;
    }

    // Guards register/login/refresh/logout (the whole of AuthController - see its
    // [ServiceFilter(typeof(RedisRateLimitFilter))] attribute) against a single client hammering
    // them; /health/live and /health/ready are mapped outside the controller (see Program.cs) and
    // never go through this filter, so orchestrator/nginx health probes can't trip it.
    //
    // Backlog item 9 (PLAN.md) moved this off Microsoft.AspNetCore.RateLimiting's in-memory
    // RateLimitPartition.GetFixedWindowLimiter and onto the shared Redis-backed RedisRateLimiter -
    // see RedisRateLimitFilter's own comment for why. FailClosed, unlike RedirectApi's own policy:
    // this guards register/login specifically against brute-force, and an open window during a
    // Redis outage means unlimited attempts - the one place in this project where "the hot path
    // must never be blocked by a dependency outage" (this project's usual default, e.g. caching,
    // the request budget) is the wrong call.
    public static WebApplicationBuilder AddAuthRateLimiting(this WebApplicationBuilder builder)
    {
        builder.AddRedisRateLimiter();

        // Read directly from configuration (not IOptions<T>) - this is consumed once,
        // synchronously, while building the policy below, so there's nothing for IOptions<T>'s
        // change-notification machinery to do here.
        var rateLimiting = builder.Configuration.GetSection("RateLimiting").Get<RateLimitingOptions>() ?? new();

        builder.Services.AddSingleton(new RateLimitPolicy(
            KeyPrefix: "auth",
            // RemoteIpAddress is null only for non-socket transports (e.g. in-process test
            // servers) - AuthApi is never reached that way outside tests, where this filter isn't
            // registered at all (see AuthControllerTests' own NewController). The "unknown" bucket
            // exists so a null here fails safe (shared, generous quota) instead of throwing.
            PartitionKeySelector: httpContext => httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            PermitLimit: rateLimiting.PermitLimit,
            Window: TimeSpan.FromSeconds(rateLimiting.WindowSeconds),
            FailureMode: RateLimitFailureMode.FailClosed));
        builder.Services.AddScoped<RedisRateLimitFilter>();

        return builder;
    }
}
