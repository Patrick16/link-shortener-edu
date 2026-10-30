using System.Text.Json;
using Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using StackExchange.Redis;

namespace Infrastructure;

public enum DeletedReason
{
    Manual,
    Expired,
}

public sealed record LinkDeletionMarker(DateTime DeletedAt, DeletedReason Reason);

// A separate, parallel Redis key from LinkCacheService's "link:{hash}" - not a field on the cached
// Link, and not written through IEntityCacheService<T> (IDistributedCache has no NX/conditional-set
// primitive at all - see RedisExtensions.AddRedisDistributedCache's own comment on InstanceName for
// why this and LinkCacheService are kept deliberately separate concerns here too). RedirectApi
// checks this key FIRST, before ever touching the main Link cache-aside path - once a hash is
// marked here, nothing else about the record matters for the redirect decision.
public interface ILinkDeletionMarkerStore
{
    // SET NX, short TTL (matches LinksController's own OptimisticCacheTtlSeconds) - can never
    // clobber a marker that's already there, whether that's a duplicate optimistic write for the
    // same hash or an already-confirmed, permanent one. Returns false when a marker already
    // existed - not an error, just "nothing to do here."
    Task<bool> TryMarkOptimisticallyAsync(string hash, DeletedReason reason, CancellationToken cancellationToken);

    // Unconditional SET, no TTL - only ever called from ShortenerService (the LinkDeletedEvent
    // consumer or the TTL sweep job) once the deletion is actually committed in Postgres. Always
    // wins over whatever was there, including a short-TTL optimistic marker - deletion is
    // terminal (a hash is never reissued), so once confirmed, the marker should never expire.
    Task ConfirmAsync(string hash, DeletedReason reason, DateTime deletedAt, CancellationToken cancellationToken);

    Task<LinkDeletionMarker?> GetAsync(string hash, CancellationToken cancellationToken);
}

public sealed class RedisLinkDeletionMarkerStore(
    IConnectionMultiplexer connectionMultiplexer,
    IConfiguration configuration,
    [FromKeyedServices(Constants.RedisCircuitBreakerKey)] ResiliencePipeline resiliencePipeline,
    ILogger<RedisLinkDeletionMarkerStore> logger) : ILinkDeletionMarkerStore
{
    // Deliberately not routed through LinkCacheService's Key()/InstanceName - this is a raw
    // IConnectionMultiplexer call, which never picks up IDistributedCache's InstanceName prefix
    // regardless (see this file's own type-level comment). "link:deleted:" is distinct enough from
    // "link:{hash}" to never collide and to read unambiguously in redis-cli.
    private static string Key(string hash) => $"link:deleted:{hash}";

    private static readonly TimeSpan OptimisticTtl = TimeSpan.FromSeconds(60);

    private readonly IConnectionMultiplexer _connectionMultiplexer = connectionMultiplexer;
    private readonly ResiliencePipeline _resilience = resiliencePipeline;
    private readonly ILogger<RedisLinkDeletionMarkerStore> _logger = logger;

    // Bug found live (2026-10-29 topology QA pass): unlike EntityCacheService's own Cache:Enabled
    // bypass (see its comment), this class had no such check at all - with caching disabled (e.g.
    // redis-master not running), every single GetAsync call below went straight to Redis and only
    // gave up once the CALLER's cancellation token fired (RedirectController's own request budget),
    // at which point the uncaught OperationCanceledException turned the request into a 503 - i.e.
    // every redirect failed, not just the optimistic-marker fast path. Same flag, same "instant,
    // clean bypass" reasoning as EntityCacheService; the resilience pipeline on top is still needed
    // for the case where caching is enabled but Redis itself is unreachable (chaos testing, or a
    // slow failover window) rather than deliberately toggled off.
    private readonly bool _cacheEnabled = !bool.TryParse(configuration["Cache:Enabled"], out var cacheEnabled) || cacheEnabled;

    public async Task<bool> TryMarkOptimisticallyAsync(string hash, DeletedReason reason, CancellationToken cancellationToken)
    {
        if (!_cacheEnabled) return false;

        try
        {
            var value = JsonSerializer.Serialize(new LinkDeletionMarker(DateTime.UtcNow, reason));
            var db = _connectionMultiplexer.GetDatabase();
            return await _resilience.ExecuteAsync(
                ct => new ValueTask<bool>(db.StringSetAsync(Key(hash), value, OptimisticTtl, When.NotExists)),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail-open, same reasoning as RedisClickCounterService: Postgres remains the source of
            // truth regardless (see RedirectController's own live DeletedAt/ExpiresAt check), so a
            // Redis hiccup here only costs the fast-path optimization, never correctness.
            _logger.LogWarning(ex, "Failed to write optimistic deletion marker for {Hash} - continuing without it", hash);
            return false;
        }
    }

    public async Task ConfirmAsync(string hash, DeletedReason reason, DateTime deletedAt, CancellationToken cancellationToken)
    {
        if (!_cacheEnabled) return;

        try
        {
            var value = JsonSerializer.Serialize(new LinkDeletionMarker(deletedAt, reason));
            var db = _connectionMultiplexer.GetDatabase();
            await _resilience.ExecuteAsync(
                ct => new ValueTask(db.StringSetAsync(Key(hash), value)), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to write confirmed deletion marker for {Hash}", hash);
        }
    }

    public async Task<LinkDeletionMarker?> GetAsync(string hash, CancellationToken cancellationToken)
    {
        if (!_cacheEnabled) return null;

        try
        {
            var db = _connectionMultiplexer.GetDatabase();
            // StackExchange.Redis's StringGetAsync has no CancellationToken overload at all (it
            // isn't cancellable from the inside), so a caller's token - e.g. RedirectController's
            // request budget - would otherwise be silently ignored here, no matter how slow a
            // connected-but-hanging Redis gets (found in review: this call was the one piece of
            // that budget's advertised coverage that wasn't actually bounded by it). The resilience
            // pipeline's own ExecuteAsync already races the call against this token.
            var value = await _resilience.ExecuteAsync(
                ct => new ValueTask<RedisValue>(db.StringGetAsync(Key(hash))), cancellationToken);
            return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<LinkDeletionMarker>((string)value!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read deletion marker for {Hash} - falling through to normal resolution", hash);
            return null;
        }
    }
}
