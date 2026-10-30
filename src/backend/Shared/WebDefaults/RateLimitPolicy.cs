using Microsoft.AspNetCore.Http;

namespace WebDefaults;

// RedisUnavailable is explicitly a per-endpoint decision, not a default (PLAN.md's backlog item
// 9's own pitfall note) - RedirectApi (the hottest read path in the whole stack, where rate
// limiting is about fairness/backpressure, not stopping an attack, and Redis already being on the
// critical path for the link-cache/deletion-marker lookups means adding a THIRD reason for Redis
// to block this path is the wrong tradeoff) chooses FailOpen; AuthApi chooses LocalFallback (see
// its own case below).
public enum RateLimitFailureMode
{
    FailOpen,
    FailClosed,

    // Falls back to ILocalFallbackRateLimiter (WebDefaults.RedisRateLimitFilter) instead of either
    // extreme - added after FailClosed turned out to mean "register/login simply don't work in any
    // topology with Redis disabled" (bug found live, 2026-10-29 topology QA pass), and plain
    // FailOpen would have reopened exactly the unbounded-brute-force window FailClosed existed to
    // close. Real throttling while Redis is down, just per-process instead of shared across
    // replicas - see LocalFallbackRateLimiter's own comment for the tradeoff this accepts.
    LocalFallback,
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
