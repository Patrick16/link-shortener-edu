using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure;

// Atomic INCR-based counter, kept separate from the IDistributedCache/EntityCacheService path
// (see LinkCacheService) since that's a JSON-blob GET/SET abstraction with no increment
// primitive. This is the hot-path write on every redirect - RedirectApi doesn't await Postgres
// for it; the durable, "my links" page count is synced from Postgres separately (ShortenerService
// consumes ClickTrackedEvent off the bus and increments Links.ClickCount there).
public class RedisClickCounterService(
    IConnectionMultiplexer connectionMultiplexer,
    ILogger<RedisClickCounterService> logger) : IClickCounterService
{
    // Sliding, not absolute - refreshed on every click (below) so a link still being clicked never
    // loses its counter. Without any TTL at all, every hash ever created keeps its own key forever,
    // even long after the link stopped being clicked - an unbounded memory leak at scale. Postgres
    // (Links.ClickCount) is the durable count regardless, so losing a cold key here is just a UI
    // number resetting to 0 on its next click, not data loss.
    private static readonly TimeSpan CounterTtl = TimeSpan.FromDays(7);

    private readonly IConnectionMultiplexer _connectionMultiplexer = connectionMultiplexer;
    private readonly ILogger<RedisClickCounterService> _logger = logger;

    public async Task IncrementAsync(string hash, CancellationToken cancellationToken)
    {
        try
        {
            var db = _connectionMultiplexer.GetDatabase();
            var key = $"link:{hash}:clicks";
            await db.StringIncrementAsync(key);
            await db.KeyExpireAsync(key, CounterTtl);
        }
        catch (RedisException ex)
        {
            // Same fail-open stance as EntityCacheService: a Redis hiccup shouldn't take the
            // redirect down with it, and this counter isn't the durable one anyway.
            _logger.LogWarning(ex, "Failed to increment Redis click counter for hash {Hash}", hash);
        }
    }
}
