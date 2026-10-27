using Microsoft.AspNetCore.Http;

namespace WebDefaults;

// RedisUnavailable is explicitly a per-endpoint decision, not a default (PLAN.md's backlog item
// 9's own pitfall note) - AuthApi (register/login, where an open window during a Redis outage
// means unlimited brute-force attempts) and RedirectApi (the hottest read path in the whole
// stack, where rate limiting is about fairness/backpressure, not stopping an attack, and Redis
// already being on the critical path for the link-cache/deletion-marker lookups means adding a
// THIRD reason for Redis to block this path is the wrong tradeoff) deliberately choose
// opposite values here.
public enum RateLimitFailureMode
{
    FailOpen,
    FailClosed,
}

// One of these per app (AuthApi, RedirectApi), registered as a singleton - see
// RedisRateLimitFilter's own comment for why one filter implementation, configured per app
// through this, beats two near-identical copies of the same Redis-check-plus-rejection-writing
// mechanics.
public sealed record RateLimitPolicy(
    // Namespaces this policy's Redis keys from any other policy's (ratelimit:{KeyPrefix}:{...}) -
    // matters once more than one service shares the same Redis instance, which every service here
    // already does.
    string KeyPrefix,
    Func<HttpContext, string> PartitionKeySelector,
    int PermitLimit,
    TimeSpan Window,
    RateLimitFailureMode FailureMode);

// Bound by hand from each app's own "RateLimiting" appsettings.json section - read once at
// startup to build that app's RateLimitPolicy, so there's nothing for IOptions<T>'s DI/live-reload
// machinery to do here. Shared between AuthApi and RedirectApi now that both build a
// RateLimitPolicy from the same two numbers (previously AuthApi-only, under AuthApi.Models).
public sealed record RateLimitingOptions
{
    public int PermitLimit { get; init; } = 100;
    public int WindowSeconds { get; init; } = 60;
}
