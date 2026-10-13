using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure;

public interface ITopLinksReader
{
    Task<IReadOnlyList<TopLinkEntry>> GetTopAsync(Guid userId, DateTime nowUtc, CancellationToken cancellationToken);
}

public sealed class RedisTopLinksReader(
    IConnectionMultiplexer connectionMultiplexer,
    ILogger<RedisTopLinksReader> logger) : ITopLinksReader
{
    // The merged result lives under a throwaway per-call key, because ZUNIONSTORE has to write its
    // output somewhere and the day buckets themselves must not be modified by a read.
    private static readonly TimeSpan ScratchKeyTtl = TimeSpan.FromSeconds(30);

    private readonly IConnectionMultiplexer _connectionMultiplexer = connectionMultiplexer;
    private readonly ILogger<RedisTopLinksReader> _logger = logger;

    public async Task<IReadOnlyList<TopLinkEntry>> GetTopAsync(Guid userId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        try
        {
            var db = _connectionMultiplexer.GetDatabase();
            var dayKeys = TopLinks.Window(DateOnly.FromDateTime(nowUtc.ToUniversalTime()))
                .Select(day => (RedisKey)TopLinks.DayKey(userId, day))
                .ToArray();
            var scratch = (RedisKey)$"clicks:user:{userId:N}:top:scratch:{Guid.NewGuid():N}";

            await db.SortedSetCombineAndStoreAsync(
                SetOperation.Union, scratch, dayKeys, aggregate: Aggregate.Sum);
            await db.KeyExpireAsync(scratch, ScratchKeyTtl);

            var ranked = await db.SortedSetRangeByRankWithScoresAsync(
                scratch, 0, TopLinks.TopCount - 1, Order.Descending);
            await db.KeyDeleteAsync(scratch);

            return ranked
                .Select(entry => new TopLinkEntry((string)entry.Element!, (long)entry.Score))
                .ToList();
        }
        catch (RedisException ex)
        {
            // Returns an empty list instead of failing the connection, so the dashboard keeps working.
            // While Redis is down it shows "no recent clicks", which is wrong but harmless, and the
            // next successful push corrects it.
            _logger.LogWarning(ex, "Failed to read top links for user {UserId}", userId);
            return [];
        }
    }
}
