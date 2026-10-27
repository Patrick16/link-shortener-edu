using System.Text.Json;
using Microsoft.Extensions.Logging;
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
    ILogger<RedisLinkDeletionMarkerStore> logger) : ILinkDeletionMarkerStore
{
    // Deliberately not routed through LinkCacheService's Key()/InstanceName - this is a raw
    // IConnectionMultiplexer call, which never picks up IDistributedCache's InstanceName prefix
    // regardless (see this file's own type-level comment). "link:deleted:" is distinct enough from
    // "link:{hash}" to never collide and to read unambiguously in redis-cli.
    private static string Key(string hash) => $"link:deleted:{hash}";

    private static readonly TimeSpan OptimisticTtl = TimeSpan.FromSeconds(60);

    private readonly IConnectionMultiplexer _connectionMultiplexer = connectionMultiplexer;
    private readonly ILogger<RedisLinkDeletionMarkerStore> _logger = logger;

    public async Task<bool> TryMarkOptimisticallyAsync(string hash, DeletedReason reason, CancellationToken cancellationToken)
    {
        try
        {
            var value = JsonSerializer.Serialize(new LinkDeletionMarker(DateTime.UtcNow, reason));
            var db = _connectionMultiplexer.GetDatabase();
            return await db.StringSetAsync(Key(hash), value, OptimisticTtl, When.NotExists);
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
        try
        {
            var value = JsonSerializer.Serialize(new LinkDeletionMarker(deletedAt, reason));
            var db = _connectionMultiplexer.GetDatabase();
            await db.StringSetAsync(Key(hash), value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to write confirmed deletion marker for {Hash}", hash);
        }
    }

    public async Task<LinkDeletionMarker?> GetAsync(string hash, CancellationToken cancellationToken)
    {
        try
        {
            var db = _connectionMultiplexer.GetDatabase();
            // StackExchange.Redis's StringGetAsync has no CancellationToken overload at all (it
            // isn't cancellable from the inside), so a caller's token - e.g. RedirectController's
            // request budget - would otherwise be silently ignored here, no matter how slow a
            // connected-but-hanging Redis gets (found in review: this call was the one piece of
            // that budget's advertised coverage that wasn't actually bounded by it). WaitAsync races
            // the real call against the token instead: on cancellation it throws immediately and
            // abandons the still-running StringGetAsync, whose eventual result is simply discarded.
            var value = await db.StringGetAsync(Key(hash)).WaitAsync(cancellationToken);
            return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<LinkDeletionMarker>((string)value!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read deletion marker for {Hash} - falling through to normal resolution", hash);
            return null;
        }
    }
}
