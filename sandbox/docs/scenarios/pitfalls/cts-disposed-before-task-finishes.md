# Disposing a `CancellationTokenSource` the instant it's "no longer needed," not once its task is actually done

**Category:** concurrency **Status:** fixed

Watch for this any time a method races two tasks against each other (or against a timeout) and each
task gets its own `CancellationTokenSource` (CTS) so the loser can be cancelled independently of the
winner. The tempting shape is `using var cts = ...` at method scope, or a bare `cts.Dispose()` the
moment you've decided you no longer need that branch. Both are wrong: "I've decided this task should
stop" and "this task has actually stopped" are different points in time, and a task racing a real
I/O call (a Mongo driver query, here) can still be mid-flight against that exact token's registration
when you dispose it out from under it — `CancellationTokenSource.Dispose()` doesn't wait for anyone
still observing the token to notice. The failure is a `ObjectDisposedException` thrown from deep
inside a library's own cancellation plumbing, somewhere far from the `Dispose()` call that actually
caused it, and it only fires when the timing lines up — which made it genuinely hard to even write a
unit test that reproduces it (see "Honestly flagged gap" below).

The same project found two separate shapes of this mistake, one review pass apart from each other —
worth knowing both exist, since fixing the first doesn't automatically prevent the second:

1. **Disposing at method scope with `using`** — guarantees disposal happens the instant the method
   returns, regardless of whether a losing branch's task is still running at that moment.
2. **Disposing a branch's own CTS synchronously, right before `await`ing that same branch's task** —
   looks safe ("I'm about to wait for it anyway"), but `Task.Delay`'s own internal cancellation
   transition is pure in-memory TPL state and reliably *wins* a race against a real external call
   noticing its own token was signalled. So at the exact line calling `Dispose()`, the task is
   usually still not done — "I'm cancelling/returning this branch" does not imply "this branch's
   task has stopped."

⚠️ **Mistake** — `src/backend/Shared/Infrastructure/HedgedClickMetaQueryService.cs` (commit
`da445ba`, shape 1 — disposal tied to method scope, not to either task's actual completion):

```csharp
internal async Task<T> RaceAsync<T>(
    Func<CancellationToken, Task<T>> first,
    Func<CancellationToken, Task<T>> second,
    TimeSpan hedgeDelay,
    CancellationToken cancellationToken)
{
    using var ctsFirst = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    using var ctsSecond = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    var firstTask = first(ctsFirst.Token);
    // ... races firstTask against a hedge-delay Task.Delay, then (if needed) starts secondTask ...

    var completed = await Task.WhenAny(pending);
    if (completed.IsCompletedSuccessfully)
    {
        // Cancels the loser, then returns - but ctsFirst/ctsSecond are BOTH disposed by the
        // `using` the instant this method returns, whether or not the loser's own task (still
        // running against the token just cancelled) has actually noticed and unwound yet.
        (completed == firstTask ? ctsSecond : ctsFirst).Cancel();
        return completed.Result;
    }
    // ...
}
```

✅ **Do this instead** (commit `8a654f1`) — dispose a task's own CTS only once that specific task is
*known* complete: immediately, if it already is; via a continuation, if it's the just-cancelled
loser and still unwinding. Look up "which CTS belongs to which task" by task identity (a dictionary),
not by a `completed == firstTask` ternary — that ternary breaks the moment a task that's already
finished (and had its CTS disposed already) needs to be identified as "the other one" again in a
later loop iteration:

```csharp
// Not `using` - a CTS backing a call that's still genuinely in flight must not be disposed out
// from under it. Each one is disposed explicitly, only once the call it belongs to has actually
// finished - immediately when that's already true, or via a continuation when it's the cancelled
// loser and still running.
var ctsByTask = new Dictionary<Task<T>, CancellationTokenSource> { [firstTask] = ctsFirst, [secondTask] = ctsSecond };
// ...
if (completed.IsCompletedSuccessfully)
{
    if (pending.Count > 0)
    {
        var loserTask = pending[0];
        var loserCts = ctsByTask[loserTask];
        loserCts.Cancel();
        // The loser's own task may still be unwinding against this exact token - don't dispose
        // until it has actually finished doing so.
        _ = loserTask.ContinueWith(_ => loserCts.Dispose(), TaskScheduler.Default);
    }
    ctsByTask[completed].Dispose(); // safe: `completed` is, by definition, already done
    return completed.Result;
}
```

That same fix commit then **reintroduced shape 2 of the exact same bug, one branch earlier, within
itself** — a new "caller already cancelled, skip starting a second query" branch added by the same
commit did `ctsFirst.Dispose(); return await firstTask;` directly, before the `await` had actually
run. `firstTask` wasn't necessarily done yet at that line — exactly the same trap, caught by a second
review pass, not by the first fix's own reasoning carrying over to a sibling branch:

```csharp
// WRONG, reintroduced in the same commit that fixed the bug above:
if (cancellationToken.IsCancellationRequested)
{
    ctsFirst.Dispose();        // firstTask may still be running against this exact token here
    return await firstTask;
}

// RIGHT (commit 11fb360, with a new regression test for this exact path):
if (cancellationToken.IsCancellationRequested)
{
    _ = firstTask.ContinueWith(_ => ctsFirst.Dispose(), TaskScheduler.Default);
    return await firstTask;
}
```

**Honestly flagged gap, worth knowing about this category of bug in general:** no unit test in this
project reproduces the actual `ObjectDisposedException` either fix addresses. Three separate attempts
(the original author, two review passes) tried to manufacture it with a mock/`TaskCompletionSource`
repro and couldn't — the real trigger needs the actual driver's (here, MongoDB's) internal
cancellation-token wiring, which a unit test's fakes don't have. Both fixes above were verified
correct by careful line-by-line re-reading against a well-documented .NET hazard (a disposed CTS's
token can still be observed mid-callback by code that registered against it first), not by a failing
test turning green. When you hit this shape of bug yourself, don't assume a red/green test cycle will
catch it reliably — plan to verify by reasoning about task-completion ordering instead, same as here.

(see `.notes/review-reports/2026-10-29-2230-hedged-reads-feature.md` and
`.notes/review-reports/2026-10-29-2244-hedged-mongo-reads-third-pass.md` — introduced in `da445ba`,
fixed in `8a654f1`, regressed and re-fixed in `11fb360`)

## Relatives

### Nodes

- [ReportingApi](node:reporting-api)
- [Mongo](node:mongo1)

### Patterns

- [Hedged reads](pattern:hedged-reads)
