using Common;
using Infrastructure;
using WebDefaults;

namespace RedirectApi;

// RedirectApi-only wiring - the parts of Program.cs that no other service shares.
public static class RedirectApiServiceExtensions
{
    public static WebApplicationBuilder AddRedirectServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IEntityCacheService<Common.Models.Link>, LinkCacheService>();
        return builder;
    }

    // Per-user click buckets for the ReportingApi top-links view. Separate from the IDistributedCache
    // registration, which is a JSON-blob abstraction with no sorted-set or pub/sub commands.
    public static WebApplicationBuilder AddClickCounter(this WebApplicationBuilder builder)
    {
        builder.AddRedisMultiplexer();
        builder.Services.AddSingleton<IClickCounterService, RedisClickCounterService>();
        return builder;
    }

    // Backlog item 9 (PLAN.md): RedirectApi, "the hottest endpoint in the whole stack", had no
    // rate limiting at all before this. Keyed purely by client IP - the plan's own original
    // framing proposed "userId when present else IP", but RedirectToOrigin never authenticates
    // its caller at all (a short-link click is an anonymous browser navigation; the userId
    // attached to a tracked click comes from the LINK's own owner, not the clicking caller - see
    // RedirectController's own ClickTrackedEvent.UserId assignment), so there is no caller userId
    // to key on here (found while implementing - corrected from the plan's draft wording).
    // FailOpen, unlike AuthApi's FailClosed: Redis is already on this path's critical dependency
    // list (the link cache, the deletion-marker lookup), and this project's established rule for
    // RedirectApi specifically is that a Redis outage degrades gracefully rather than blocking the
    // hot path (see the request-budget feature's own design) - adding a third reason for Redis to
    // block redirects would contradict that.
    public static WebApplicationBuilder AddRedirectRateLimiting(this WebApplicationBuilder builder)
    {
        builder.AddRedisRateLimiter();

        var rateLimiting = builder.Configuration.GetSection("RateLimiting").Get<RateLimitingOptions>() ?? new();

        builder.Services.AddSingleton(new RateLimitPolicy(
            KeyPrefix: "redirect",
            // Populated by UseForwardedHeaders (see Program.cs) from nginx's own X-Forwarded-For
            // hop only - RedirectApi has no host port mapping (nginx is the only way in from
            // outside the Docker network), so RemoteIpAddress is nginx's own container address
            // without it. The "unknown" bucket mirrors AuthApi's identical fail-safe reasoning.
            PartitionKeySelector: httpContext => httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            PermitLimit: rateLimiting.PermitLimit,
            Window: TimeSpan.FromSeconds(rateLimiting.WindowSeconds),
            FailureMode: RateLimitFailureMode.FailOpen));
        builder.Services.AddScoped<RedisRateLimitFilter>();

        return builder;
    }
}
