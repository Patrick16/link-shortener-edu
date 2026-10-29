using Common.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure;

// Hedged read for ReportingApi's GET /reports/{hash}/clicks: races the same query against two of
// clicks_meta_db's CURRENT secondaries (whichever two those are - the caller already excluded the
// real primary live, not by assuming it's always mongo1; see MongoExtensions.
// AddMongoClickMetaReader's own comment) to cut tail latency on a slow replica, instead of waiting
// on whichever one happened to get picked. This is NOT what Mongo's own
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
        // Not `using` - a CTS backing a call that's still genuinely in flight must not be disposed
        // out from under it (found in review: disposing at method scope raced the loser's own,
        // still-unwinding Mongo call against the exact token it was cancelled with). Each one is
        // disposed explicitly below, only once the call it belongs to has actually finished -
        // immediately when that's already true, or via a continuation when it's the cancelled
        // loser and still running.
        var ctsFirst = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var firstTask = first(ctsFirst.Token);

        var delayTask = Task.Delay(hedgeDelay, cancellationToken);
        var completedEarly = await Task.WhenAny(firstTask, delayTask);
        if (completedEarly == firstTask && firstTask.IsCompletedSuccessfully)
        {
            // The common case: first answered inside the hedge delay - second never needs to
            // exist at all. firstTask is already done, so disposing its CTS here can't race
            // anything still in flight against it.
            ctsFirst.Dispose();
            return await firstTask;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // The caller itself gave up (e.g. the HTTP request was aborted) in this same window -
            // no point firing a second query nobody will ever read the result of. Let the natural
            // cancellation surface through firstTask's own linked token instead.
            //
            // Found in review (a regression of F1 within this very fix): firstTask is NOT
            // necessarily done yet here - Task.Delay's own cancellation transition is pure TPL
            // state and reliably wins the race against a real in-flight Mongo call noticing its
            // token was cancelled. Disposing ctsFirst synchronously, right here, raced that
            // still-unwinding call exactly like the loser-disposal bug this whole file exists to
            // avoid - the same continuation-based disposal used for the loser below is needed here
            // too, not a direct `Dispose()` before the await.
            _ = firstTask.ContinueWith(_ => ctsFirst.Dispose(), TaskScheduler.Default);
            return await firstTask;
        }

        // Either the hedge delay elapsed with first still outstanding, or first already finished
        // but FAULTED within the delay - either way, a second replica is worth trying before
        // giving up (a quick failure is not a reason to skip the fallback).
        logger.LogDebug("Racing a second replica for this read ({Reason})", completedEarly == firstTask ? "first replica errored" : "hedge delay elapsed");
        var ctsSecond = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var secondTask = second(ctsSecond.Token);

        // Looked up by task identity, not by a `completed == firstTask` ternary - once a task has
        // been removed from `pending` and its CTS disposed (the fault branch below), a LATER
        // iteration must never try to re-derive "which CTS belongs to the other one" from that
        // same ternary, or it ends up calling Cancel() on an already-disposed source (found by the
        // tests this fix landed with, not just reasoned about).
        var ctsByTask = new Dictionary<Task<T>, CancellationTokenSource> { [firstTask] = ctsFirst, [secondTask] = ctsSecond };
        var pending = new List<Task<T>> { firstTask, secondTask };
        while (true)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            var completedCts = ctsByTask[completed];

            if (completed.IsCompletedSuccessfully)
            {
                // If the other call is still in `pending`, it's genuinely still outstanding -
                // cancel it (never the one that just won), but don't dispose its CTS right here:
                // it may still be mid-flight against that exact token. The continuation disposes it
                // once that call has actually finished unwinding, whenever that turns out to be.
                // If it's NOT still in `pending`, it already faulted in an earlier iteration and its
                // CTS was already disposed there - nothing left to do for it.
                if (pending.Count > 0)
                {
                    var loserTask = pending[0];
                    var loserCts = ctsByTask[loserTask];
                    loserCts.Cancel();
                    _ = loserTask.ContinueWith(_ => loserCts.Dispose(), TaskScheduler.Default);
                }

                completedCts.Dispose();
                return completed.Result;
            }

            // This one faulted (or was cancelled) - already finished, so disposing its own CTS
            // right here is safe. Give the other call a chance before giving up, same reasoning as
            // the header comment: a single slow-but-alive replica shouldn't be worse off than a
            // single dead one.
            completedCts.Dispose();
            if (pending.Count == 0)
            {
                return await completed; // rethrows the real exception/cancellation
            }
        }
    }
}
