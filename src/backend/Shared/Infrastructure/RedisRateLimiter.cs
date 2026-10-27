using Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using StackExchange.Redis;

namespace Infrastructure;

public enum RateLimitOutcome
{
    Allowed,
    Denied,
    RedisUnavailable,
}

// Redis-backed fixed-window counter (PLAN.md's backlog item 9 proposed this as a "sliding
// window" - what actually got built, and what fits a single atomic Redis round trip, is a FIXED
// window instead: a per-key counter with a TTL equal to the window itself, reset by whichever
// request happens to be first in a fresh cycle. Same algorithm AuthApi's own prior in-memory
// FixedWindowRateLimiter already used - this only moves where the count lives, not what kind of
// window it is). Never throws on a Redis failure - the caller (see RedisRateLimitFilter in
// WebDefaults) decides whether that means fail-open or fail-closed, since that's explicitly a
// per-endpoint decision, not a default (this item's own pitfall note).
public interface IRedisRateLimiter
{
    Task<RateLimitOutcome> CheckAsync(string key, int permitLimit, TimeSpan window, CancellationToken cancellationToken);
}

public sealed class RedisRateLimiter(
    IConnectionMultiplexer connectionMultiplexer,
    [FromKeyedServices(Constants.RedisCircuitBreakerKey)] ResiliencePipeline resiliencePipeline,
    ILogger<RedisRateLimiter> logger) : IRedisRateLimiter
{
    // INCR, then PEXPIRE only on the very first increment of a fresh key (current == 1) - a
    // single Lua script keeps "increment + maybe set the window's TTL" atomic. Splitting this
    // into two round trips would leave a window where a crash/race between them leaves a key
    // that was incremented once but never got a TTL, permanently blocking that bucket instead of
    // it ever resetting - a classic, easy-to-miss Redis rate-limiter bug.
    private const string IncrementAndMaybeExpireScript = """
        local current = redis.call("INCR", KEYS[1])
        if current == 1 then
            redis.call("PEXPIRE", KEYS[1], ARGV[1])
        end
        return current
        """;

    private readonly IConnectionMultiplexer _connectionMultiplexer = connectionMultiplexer;
    private readonly ResiliencePipeline _resilience = resiliencePipeline;
    private readonly ILogger<RedisRateLimiter> _logger = logger;

    public async Task<RateLimitOutcome> CheckAsync(string key, int permitLimit, TimeSpan window, CancellationToken cancellationToken)
    {
        try
        {
            var db = _connectionMultiplexer.GetDatabase();
            var current = (long)await _resilience.ExecuteAsync(
                ct => new ValueTask<RedisResult>(db.ScriptEvaluateAsync(
                    IncrementAndMaybeExpireScript,
                    [(RedisKey)key],
                    [(RedisValue)(long)window.TotalMilliseconds])),
                cancellationToken);

            return current <= permitLimit ? RateLimitOutcome.Allowed : RateLimitOutcome.Denied;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail-open at THIS layer always - returned as a distinct outcome rather than thrown
            // or silently mapped to Allowed/Denied here, so every caller makes its own
            // fail-open/fail-closed choice explicitly instead of inheriting whatever this class
            // happened to pick.
            _logger.LogWarning(ex, "Rate limit check failed for key {Key} - caller decides fail-open/fail-closed", key);
            return RateLimitOutcome.RedisUnavailable;
        }
    }
}
