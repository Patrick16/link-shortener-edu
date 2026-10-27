using System.Collections.Concurrent;

namespace ShortenerService;

// Buffers click-count increments in-process across an entire flush window (ClickCountFlushWorker's
// ~500ms interval), not just one consumed RabbitMQ batch - collapses however many batches arrive
// for the same hot hash within that window into a single UPDATE instead of one per batch. This is
// what actually relieves row-lock contention on a viral link's row under multiple ShortenerService
// replicas (PLAN.md's backlog item 8: ExecuteUpdateAsync already fixed the lost-update RACE, but
// every replica was still issuing its own competing UPDATE per batch, serializing on Postgres's
// row lock under load). Shared as a singleton between ClickTrackedConsumer (producer) and
// ClickCountFlushWorker (consumer).
//
// Anything still buffered here when the process crashes or is force-killed is simply lost - the
// same accepted approximate-display-counter tradeoff ClickTrackedConsumer's own comment already
// documents for Links.ClickCount (TrafficService's Clicks table remains the real audit trail). No
// flush-on-shutdown mechanism is built for the same reason: it would only narrow, never close, that
// already-accepted window, for a number that's explicitly allowed to drift.
public sealed class ClickCountAggregator
{
    private readonly ConcurrentDictionary<string, long> _pendingCounts = new();

    public void Add(string hash, int count) =>
        _pendingCounts.AddOrUpdate(hash, count, (_, existing) => existing + count);

    // TryRemove per key (not Clear()) - a concurrent Add for a hash that lands between this
    // method's snapshot of the keys and that key's own TryRemove either lands before the removal
    // (included in the drained value) or after it (TryRemove already failed to find a replacement
    // entry, so AddOrUpdate's "add" branch recreates the key fresh) - either way nothing is lost,
    // it just carries over to the NEXT flush instead of racing this one. ConcurrentDictionary's own
    // per-bucket locking is what makes both orderings safe.
    public Dictionary<string, long> DrainPending()
    {
        var drained = new Dictionary<string, long>();
        foreach (var hash in _pendingCounts.Keys.ToArray())
        {
            if (_pendingCounts.TryRemove(hash, out var count))
            {
                drained[hash] = count;
            }
        }

        return drained;
    }
}
