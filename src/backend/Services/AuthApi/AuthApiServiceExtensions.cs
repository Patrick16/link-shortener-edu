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
    // see RedisRateLimitFilter's own comment for why. LocalFallback, unlike RedirectApi's own
    // FailOpen policy: this guards register/login specifically against brute-force, so a Redis
    // outage must not open an unlimited window - but plain FailClosed (this policy's choice until a
    // live topology QA pass, 2026-10-29, found it meant register/login simply didn't work at all in
    // any topology with Redis disabled) turned out to be the wrong tradeoff too, for an educational
    // sandbox whose whole point is letting every toggle combination actually be explored. See
    // RateLimitFailureMode.LocalFallback/LocalFallbackRateLimiter's own comments for what this
    // degrades to instead (real throttling, just per-process, not shared across replicas).
    public static WebApplicationBuilder AddAuthRateLimiting(this WebApplicationBuilder builder)
    {
        builder.AddRedisRateLimiter();
        // Found in review: AuthApi had no warmup for the raw multiplexer AddRedisRateLimiter
        // registers, unlike LinkApi/RedirectApi/ShortenerService's IDistributedCache connection (see
        // RedisWarmupService's own comment on why this matters - the exact same connection-
        // establishment tax, just on this service's rate limiter instead of its cache). Safe to add
        // here without pulling in AddRedisDistributedCache's cache/memory-cache/InstanceName baggage
        // this class's own comment on AddRedisRateLimiter already deliberately avoids for AuthApi -
        // RedisWarmupService resolves IDistributedCache optionally (IServiceProvider.GetService, not
        // a constructor dependency), so it degrades to warming the multiplexer alone when nothing
        // registered one, exactly AuthApi's case.
        builder.Services.AddHostedService<RedisWarmupService>();

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
            FailureMode: RateLimitFailureMode.LocalFallback));
        builder.Services.AddScoped<RedisRateLimitFilter>();

        return builder;
    }
}
