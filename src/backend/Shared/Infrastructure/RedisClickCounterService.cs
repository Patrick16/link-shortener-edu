using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure;

// Atomic INCR-based counter, kept separate from the IDistributedCache/EntityCacheService path
// (see LinkCacheService) since that's a JSON-blob GET/SET abstraction with no increment
// primitive. This is the hot-path write on every redirect - RedirectApi doesn't await Postgres
// for it. LinkApi's GetLinks reads this back as the "fresh" half of a Max(Postgres, Redis) display
// count (see LinksController.GetLinks) - Postgres stays the durable floor, since this counter's
// own TTL (below) can reset a long-dormant link's count back down to a small number.
public class RedisClickCounterService(
    IConnectionMultiplexer connectionMultiplexer,
    ILogger<RedisClickCounterService> logger) : IClickCounterService
{
    // Sliding, not absolute - refreshed on every click (below) so a link still being clicked never
    // loses its counter. Without any TTL at all, every hash ever created keeps its own key forever,
    // even long after the link stopped being clicked - an unbounded memory leak at scale. A reset
    // after 7 idle days only ever shows up as a dip back toward (never below) Postgres's own count,
    // not a visible regression - see GetLinks' Max combine.
    private static readonly TimeSpan CounterTtl = TimeSpan.FromDays(7);

    private readonly IConnectionMultiplexer _connectionMultiplexer = connectionMultiplexer;
    private readonly ILogger<RedisClickCounterService> _logger = logger;

    public async Task IncrementAsync(string hash, CancellationToken cancellationToken)
    {
        try
        {
            var db = _connectionMultiplexer.GetDatabase();
            var key = Key(hash);
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

    public async Task<long?> GetAsync(string hash, CancellationToken cancellationToken)
    {
        try
        {
            var db = _connectionMultiplexer.GetDatabase();
            var value = await db.StringGetAsync(Key(hash));
            return value.IsNull ? null : (long)value;
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Failed to read Redis click counter for hash {Hash}", hash);
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, long>> GetManyAsync(
        IReadOnlyCollection<string> hashes, CancellationToken cancellationToken)
    {
        if (hashes.Count == 0)
        {
            return new Dictionary<string, long>();
        }

        try
        {
            var db = _connectionMultiplexer.GetDatabase();
            var hashList = hashes as IReadOnlyList<string> ?? hashes.ToList();
            var keys = hashList.Select(h => (RedisKey)Key(h)).ToArray();

            // One MGET round trip for the whole page instead of one GET per hash.
            var values = await db.StringGetAsync(keys);

            var result = new Dictionary<string, long>(hashList.Count);
            for (var i = 0; i < hashList.Count; i++)
            {
                if (!values[i].IsNull)
                {
                    result[hashList[i]] = (long)values[i];
                }
            }

            return result;
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Failed to batch-read Redis click counters for {Count} hash(es)", hashes.Count);
            return new Dictionary<string, long>();
        }
    }

    private static string Key(string hash) => $"link:{hash}:clicks";
}
