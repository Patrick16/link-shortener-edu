# A per-request deadline's catch filter has to check the caller's own token, not its own local one

**Category:** concurrency **Status:** fixed

Watch for this any time a per-request deadline (its own `CancellationTokenSource`, armed with
`CancelAfter`) wraps a call into code that does its own request coalescing — a cache's in-flight-
fetch de-duplication, a singleflight pattern, anything where concurrent callers for the same key
share one underlying operation. The deadline that actually fires may not be *this* request's own;
it can be whichever concurrent caller's token happened to be the one driving the shared operation.
A catch filter that only recognizes its own locally-created token will miss every other caller's
deadline trip and let the exception propagate uncaught.

**Concrete instance in this project:** `RedirectController`'s request budget wraps the
deletion-marker lookup and cache-or-DB fetch in a linked `CancellationTokenSource`
(`budgetToken`), armed with `CancelAfter(_requestBudget)`. `EntityCacheService.GetOrFetch` coalesces
concurrent misses for the *same* cache key behind one shared `Lazy<Task<T?>>` — whichever request's
call wins the race to create it becomes the "driver" and runs the real fetch under *its own*
`budgetToken`; every other concurrent request for that key is a "joiner" that just awaits the
driver's task. When the driver's budget fires, every joiner observes the exact same
`OperationCanceledException`, tied to the *driver's* `CancellationTokenSource` — never to any
joiner's own local `budgetToken`. A catch filter that only checks `budgetToken.IsCancellationRequested`
is true for the driver and false for every joiner, so a joiner's exception propagated straight
through the `catch` and out as an unhandled exception — a 500, not the intended clean 503 — even
though its own budget was nowhere near firing. This only reproduces under genuinely concurrent
requests for the same cache key during a cache miss (a thundering-herd scenario), so it's easy to
miss in isolated manual testing and was only caught by a dedicated concurrent-request regression
test.

⚠️ **Mistake** — `src/backend/Services/RedirectApi/Controllers/RedirectController.cs` (commit
`6174662`):

```csharp
catch (OperationCanceledException) when (budgetToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
{
    // The budget fired, not the caller disconnecting ...
}
```

✅ **Do this instead** (`e9bf63d`) — check the caller's own token instead of the locally-created
one. That token is false for every request (driver or joiner) unless *that specific client*
disconnected, so "a budget fired somewhere in this fetch" is correctly caught for all of them,
while a genuine client disconnect (where `cancellationToken.IsCancellationRequested` is already
true) is deliberately left uncaught — ASP.NET Core's own exception-handling middleware already
handles that case correctly, and there's no client left to send a 503 to:

```csharp
catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
{
    // Not "budgetToken.IsCancellationRequested" - _cache.GetOrFetch coalesces concurrent misses
    // for the same hash behind one shared in-flight Task, driven by whichever request's call
    // wins the race. When that driver's OWN budget fires, every joiner sees the SAME
    // OperationCanceledException, tied to the driver's CancellationTokenSource, not this
    // request's local budgetToken - so checking only the local token here would miss every
    // joiner and let the exception propagate uncaught (found in review). Checking the caller's
    // own cancellationToken instead correctly tells "a budget fired somewhere in this fetch"
    // apart from "this specific client disconnected".
}
```

**Provenance:** found by `feature-review` on commit `6174662`, fixed in `e9bf63d`, with a dedicated
concurrent-request regression test added in the same fix.

## Relatives

### Nodes

- [RedirectApi](node:redirect-api)

### Patterns

- [Caching](pattern:caching) — `EntityCacheService.GetOrFetch`'s in-flight-fetch coalescing is the
  other half of this interaction
