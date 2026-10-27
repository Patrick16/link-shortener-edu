# Accepting a `CancellationToken` parameter doesn't mean the call underneath actually honors it

**Category:** resilience-gap **Status:** fixed

Watch for this any time a method takes a `CancellationToken` and passes it straight through to a
library call that *looks* cancellable (it's an `async` method on an otherwise-modern client) but
has no overload that actually accepts one. The method signature promises cancellation; the real
behavior silently doesn't deliver it, and nothing about the code — or a quick glance at the
signature — tells you that.

**Concrete instance in this project:** `RedisLinkDeletionMarkerStore.GetAsync` accepted a
`CancellationToken` and passed through to `StackExchange.Redis`'s `StringGetAsync` — which has no
`CancellationToken` overload at all. The token was a real parameter, but the method it was handed
to never consulted it. `RedirectController`'s own request-budget comment claimed this lookup was
bounded by the budget; it wasn't — a hanging-but-connected Redis would never actually be cut off,
no matter how long `StringGetAsync` took, because there was nothing in the call chain capable of
interrupting it.

⚠️ **Mistake** — `src/backend/Shared/Infrastructure/LinkDeletionMarkerStore.cs` (commit `6174662`):

```csharp
var db = _connectionMultiplexer.GetDatabase();
var value = await db.StringGetAsync(Key(hash));
return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<LinkDeletionMarker>((string)value!);
```

✅ **Do this instead** (`e9bf63d`) — race the call against the token with `Task.WaitAsync`, which
lets a caller observe cancellation even when the underlying task has no cancellation support of
its own. On cancellation it throws immediately and simply abandons the still-running
`StringGetAsync`; its eventual result (if it ever completes) is discarded, never awaited:

```csharp
// StackExchange.Redis's StringGetAsync has no CancellationToken overload at all (it isn't
// cancellable from the inside), so a caller's token - e.g. RedirectController's request
// budget - would otherwise be silently ignored here, no matter how slow a connected-but-hanging
// Redis gets (found in review: this call was the one piece of that budget's advertised coverage
// that wasn't actually bounded by it). WaitAsync races the real call against the token instead:
// on cancellation it throws immediately and abandons the still-running StringGetAsync, whose
// eventual result is simply discarded.
var value = await db.StringGetAsync(Key(hash)).WaitAsync(cancellationToken);
```

`Task.WaitAsync` **abandons, not cancels**, the underlying call — this is fine for a read with no
side effect (as here), but the same pattern must not be applied unthinkingly to a write whose
completion matters even after the caller stops waiting on it; abandoning a write-in-flight can
leave its outcome unknown rather than merely "not waited for."

**Provenance:** found by `feature-review` on commit `6174662`, fixed in `e9bf63d`, with a
regression test (a never-completing `TaskCompletionSource` simulating a hung-but-connected Redis
call) added in the same fix.

## Relatives

### Nodes

- [RedirectApi](node:redirect-api)
- [Redis](node:redis-master)

### Patterns

- [Caching](pattern:caching)
