using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure;

public enum IdempotencyStatus
{
    Pending,
    Completed,
}

// StatusCode/ContentType/BodyJson are only ever set once Status is Completed - the first caller's
// exact prior response, replayed verbatim to a retry with the same key instead of redoing the
// real work (and, for POST /Links specifically, instead of minting a second link for what was
// logically one request).
public sealed record IdempotencyRecord(IdempotencyStatus Status, int? StatusCode = null, string? ContentType = null, string? BodyJson = null);

// Same two-tier shape as LinkDeletionMarkerStore (TryMarkOptimisticallyAsync/ConfirmAsync) -
// deliberately mirrored, not a coincidence: a short-TTL optimistic claim first, then an
// unconditional overwrite with the real, long-TTL outcome once it's known. Here that split also
// solves a problem the deletion marker doesn't have: if the first caller's process dies mid-request
// (crash, kill -9, a lost connection) before ever reaching CompleteAsync/EvictAsync, a claim held
// at the full 24h idempotency TTL would leave every retry 409-Pending for up to a day. Claiming at
// a short TTL instead means a crashed request's claim simply expires soon, and the next retry
// claims fresh and actually does the work.
public interface IIdempotencyStore
{
    // SET NX at the short in-flight TTL. True = this caller won the race and should do the real
    // work; false = a record (pending or completed) already exists - inspect it via GetAsync.
    Task<bool> TryClaimAsync(string key, CancellationToken cancellationToken);

    Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken);

    // Unconditional overwrite at the full 24h TTL - only ever called by the caller that won
    // TryClaimAsync's race, once its real response is known.
    Task CompleteAsync(string key, int statusCode, string contentType, string bodyJson, CancellationToken cancellationToken);

    // Releases a claim this caller won but never completed (the real work threw) - without this,
    // a single failed attempt would permanently poison the key for the rest of the in-flight TTL,
    // rejecting even a legitimate retry as "already in progress" for something that never actually
    // started.
    Task EvictAsync(string key, CancellationToken cancellationToken);
}

public sealed class RedisIdempotencyStore(
    IConnectionMultiplexer connectionMultiplexer,
    ILogger<RedisIdempotencyStore> logger) : IIdempotencyStore
{
    private static string Key(string idempotencyKey) => $"idempotency:links-create:{idempotencyKey}";

    private static readonly TimeSpan InFlightTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CompletedTtl = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IConnectionMultiplexer _connectionMultiplexer = connectionMultiplexer;
    private readonly ILogger<RedisIdempotencyStore> _logger = logger;

    public async Task<bool> TryClaimAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var value = JsonSerializer.Serialize(new IdempotencyRecord(IdempotencyStatus.Pending), JsonOptions);
            var db = _connectionMultiplexer.GetDatabase();
            return await db.StringSetAsync(Key(key), value, InFlightTtl, When.NotExists);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail-open, same reasoning as every other Redis-backed store in this codebase
            // (EntityCacheService, RedisLinkDeletionMarkerStore): Redis is an optimization layer
            // here, never a hard dependency for the write path. A Redis outage means POST /Links
            // loses its idempotency guarantee for the duration, not that it starts failing - so
            // this returns true ("proceed, you've won the claim") rather than blocking the request.
            _logger.LogWarning(ex, "Failed to claim idempotency key {Key} - proceeding without an idempotency guarantee", key);
            return true;
        }
    }

    public async Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var db = _connectionMultiplexer.GetDatabase();
            var value = await db.StringGetAsync(Key(key));
            return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<IdempotencyRecord>((string)value!, JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read idempotency record for {Key} - treating as absent", key);
            return null;
        }
    }

    public async Task CompleteAsync(string key, int statusCode, string contentType, string bodyJson, CancellationToken cancellationToken)
    {
        try
        {
            var value = JsonSerializer.Serialize(new IdempotencyRecord(IdempotencyStatus.Completed, statusCode, contentType, bodyJson), JsonOptions);
            var db = _connectionMultiplexer.GetDatabase();
            await db.StringSetAsync(Key(key), value, CompletedTtl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The real work already happened (and already returned to the caller) regardless of
            // whether this write lands - losing this just means a retry within the TTL window
            // won't find a completed record and will redo the work instead of replaying, same
            // "degrades to no idempotency guarantee" story as every failure mode here.
            _logger.LogWarning(ex, "Failed to persist idempotency result for {Key} - a retry may redo the work instead of replaying it", key);
        }
    }

    public async Task EvictAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var db = _connectionMultiplexer.GetDatabase();
            await db.KeyDeleteAsync(Key(key));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Worst case here is the in-flight claim simply expires on its own after InFlightTtl -
            // a delayed self-heal, not a stuck key, so this is a warning, not an escalation.
            _logger.LogWarning(ex, "Failed to evict idempotency claim for {Key} - it will self-expire after {Ttl}", key, InFlightTtl);
        }
    }
}
