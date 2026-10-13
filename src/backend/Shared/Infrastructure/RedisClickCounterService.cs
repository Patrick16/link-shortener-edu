using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure;

// Records every owned click into that owner's current day bucket (a sorted set of hash -> clicks)
// and announces the change on TopLinks.ChangedChannel. One batch per click keeps this to a single
// round trip on the redirect hot path. Postgres's Links.ClickCount is still the durable lifetime
// total, synced separately off the bus - this only feeds the rolling "top links" view.
public class RedisClickCounterService(
    IConnectionMultiplexer connectionMultiplexer,
    ILogger<RedisClickCounterService> logger) : IClickCounterService
{
    private static readonly TimeSpan DayKeyTtl = TimeSpan.FromDays(TopLinks.DayKeyTtlDays);

    private readonly IConnectionMultiplexer _connectionMultiplexer = connectionMultiplexer;
    private readonly ILogger<RedisClickCounterService> _logger = logger;

    public async Task RecordClickAsync(string hash, Guid? ownerId, DateTime clickedAtUtc, CancellationToken cancellationToken)
    {
        if (ownerId is not { } userId)
        {
            return;
        }

        try
        {
            var db = _connectionMultiplexer.GetDatabase();
            var key = TopLinks.DayKey(userId, DateOnly.FromDateTime(clickedAtUtc.ToUniversalTime()));

            var batch = db.CreateBatch();
            var increment = batch.SortedSetIncrementAsync(key, hash, 1);
            var expire = batch.KeyExpireAsync(key, DayKeyTtl);
            var publish = batch.PublishAsync(
                RedisChannel.Literal(TopLinks.ChangedChannel), userId.ToString("N"));
            batch.Execute();

            await Task.WhenAll(increment, expire, publish);
        }
        catch (RedisException ex)
        {
            // Same fail-open stance as EntityCacheService: a Redis hiccup shouldn't take the redirect
            // down with it. The lost click only costs one entry in a rolling top list.
            _logger.LogWarning(ex, "Failed to record click for hash {Hash} in the top-links counter", hash);
        }
    }
}
