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

        Task increment = Task.CompletedTask, expire = Task.CompletedTask, publish = Task.CompletedTask;
        try
        {
            var db = _connectionMultiplexer.GetDatabase();
            var key = TopLinks.DayKey(userId, DateOnly.FromDateTime(clickedAtUtc.ToUniversalTime()));

            var batch = db.CreateBatch();
            increment = batch.SortedSetIncrementAsync(key, hash, 1);
            expire = batch.KeyExpireAsync(key, DayKeyTtl);
            publish = batch.PublishAsync(
                RedisChannel.Literal(TopLinks.ChangedChannel), userId.ToString("N"));
            batch.Execute();

            await Task.WhenAll(increment, expire, publish);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail-open: a Redis failure of any kind (including timeouts, which don't derive from
            // RedisException) must not turn an already-dispatched click into a failed redirect. The
            // lost click only costs one entry in a rolling top list.
            _logger.LogWarning(ex, "Failed to record click for hash {Hash} in the top-links counter", hash);
        }
        finally
        {
            // If batch.Execute() itself threw (e.g. the connection was already down), the three
            // tasks above were created but never reached Task.WhenAll - each still completes on its
            // own later (faulted or not), and an unobserved fault on a Task that's been garbage
            // collected surfaces as an UnobservedTaskException. Observing them here is a no-op in
            // the normal case where Task.WhenAll already awaited them.
            ObserveIfFaulted(increment);
            ObserveIfFaulted(expire);
            ObserveIfFaulted(publish);
        }
    }

    private static void ObserveIfFaulted(Task task) =>
        task.ContinueWith(
            static t => _ = t.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
}
