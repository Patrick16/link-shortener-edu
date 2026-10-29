using Common.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure;

// Hedged read for ReportingApi's GET /reports/{hash}/clicks: races the same query against two of
// clicks_meta_db's three rs0 members (mongo2/mongo3, deliberately not the primary - see
// MongoExtensions.AddMongoClickMetaReader's own comment for why) to cut tail latency on a slow
// replica, instead of waiting on whichever one happened to get picked. This is NOT what Mongo's own
// readPreference=secondaryPreferred does - that policy still only ever queries ONE node per
// request; this class is the app-level mechanism that actually races two of them.
//
// Falls back to a single plain query (no hedging) whenever fewer than 2 direct-to-replica clients
// are available - e.g. while the Mongo-replicas toggle is off and rs0 only has mongo1, there's
// nothing to hedge against.
public sealed class HedgedClickMetaQueryService(
    IClickMetaQueryService fallback,
    IReadOnlyList<IClickMetaQueryService> hedgeNodes,
    TimeSpan hedgeDelay,
    ILogger<HedgedClickMetaQueryService> logger) : IClickMetaQueryService
{
    public Task<bool> PingAsync(CancellationToken cancellationToken = default) => fallback.PingAsync(cancellationToken);

    public Task<IReadOnlyList<ClickDetail>> GetRecentAsync(
        string hash, Guid callerUserId, int limit, CancellationToken cancellationToken = default)
    {
        if (hedgeNodes.Count < 2)
        {
            return fallback.GetRecentAsync(hash, callerUserId, limit, cancellationToken);
        }

        return RaceAsync(
            ct => hedgeNodes[0].GetRecentAsync(hash, callerUserId, limit, ct),
            ct => hedgeNodes[1].GetRecentAsync(hash, callerUserId, limit, ct),
            hedgeDelay,
            cancellationToken);
    }

    // Races `first` against `second`, deferring `second` by `hedgeDelay` unless `first` finishes
    // sooner - the actual hedged-request mechanism (see this class's own header comment for what
    // it's for and why it's different from Mongo's own readPreference). Returns whichever call
    // completes first; the other is cancelled once a winner is known, never interrupted before
    // that. If the call Task.WhenAny hands back first actually faulted, falls through to the other
    // instead of propagating immediately - a single slow-but-alive replica shouldn't be worse off
    // than a single dead one.
    internal async Task<T> RaceAsync<T>(
        Func<CancellationToken, Task<T>> first,
        Func<CancellationToken, Task<T>> second,
        TimeSpan hedgeDelay,
        CancellationToken cancellationToken)
    {
        using var ctsFirst = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var ctsSecond = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var firstTask = first(ctsFirst.Token);

        var delayTask = Task.Delay(hedgeDelay, cancellationToken);
        var completedEarly = await Task.WhenAny(firstTask, delayTask);
        if (completedEarly == firstTask && firstTask.IsCompletedSuccessfully)
        {
            // The common case: first answered inside the hedge delay - second never needs to
            // exist at all, so there's nothing to cancel either.
            return await firstTask;
        }

        // Either the hedge delay elapsed with first still outstanding, or first already finished
        // but FAULTED within the delay - either way, a second replica is worth trying before
        // giving up (a quick failure is not a reason to skip the fallback).
        logger.LogDebug("Racing a second replica for this read ({Reason})", completedEarly == firstTask ? "first replica errored" : "hedge delay elapsed");
        var secondTask = second(ctsSecond.Token);

        var pending = new List<Task<T>> { firstTask, secondTask };
        while (true)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);

            if (completed.IsCompletedSuccessfully)
            {
                // Cancel whichever call is still outstanding - never the one that just won.
                (completed == firstTask ? ctsSecond : ctsFirst).Cancel();
                return completed.Result;
            }

            // This one faulted (or was cancelled) - give the other call a chance before giving up,
            // same reasoning as the header comment: a single slow-but-alive replica shouldn't be
            // worse off than a single dead one.
            if (pending.Count == 0)
            {
                return await completed; // rethrows the real exception/cancellation
            }
        }
    }
}
