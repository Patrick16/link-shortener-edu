# Accepting a `CancellationToken` parameter doesn't mean the call underneath actually honors it

**Category:** resilience-gap **Status:** fixed

Watch for this any time a method takes a `CancellationToken` and passes it straight through to a
library call that *looks* cancellable (it's an `async` method on an otherwise-modern client) but
has no overload that actually accepts one. The method signature promises cancellation; the real
behavior silently doesn't deliver it, and nothing about the code — or a quick glance at the
signature — tells you that.

A second, easy-to-miss trap sits one layer up, and is the reason this doc was revised: wrapping the
call in a Polly `ResiliencePipeline.ExecuteAsync` does **not** fix this by itself. `ExecuteAsync`
only races its callback against the token when a *timeout strategy* is actually chained into the
pipeline — a circuit-breaker-only (or retry-only) pipeline just awaits the callback's task directly
and never observes cancellation while that task is still pending. The method signature still
"accepts" the token, the code still compiles, and nothing throws — so it's entirely possible to
convince yourself a generic resilience wrapper already handles cancellation when it doesn't, which
is exactly what happened here the first time around.

**Concrete instance in this project:** all three methods of `RedisLinkDeletionMarkerStore`
(`TryMarkOptimisticallyAsync`, `ConfirmAsync`, `GetAsync`) accepted a `CancellationToken`, passed it
to a circuit-breaker-only `ResiliencePipeline.ExecuteAsync`, and from there into `StackExchange.
Redis` calls (`StringSetAsync`/`StringGetAsync`) that have no `CancellationToken` overload at all.
`RedirectController`'s own request-budget comment claimed the `GetAsync` lookup was bounded by the
budget; it wasn't — a hanging-but-connected Redis would never actually be cut off, no matter how
long the call took, because nothing in the chain (not the Redis call, not the pipeline wrapping it)
was capable of interrupting it. The first fix only closed `GetAsync`, on the belief that
`ExecuteAsync` "already races the call against this token" — false, per the paragraph above, and
the exact same gap existed unnoticed in the other two methods for the same reason. Confirmed
empirically, not just by reading the diff: running `GetAsync`'s own pre-existing cancellation
regression test against the unfixed code made it hang indefinitely, requiring the test host process
to be killed.

⚠️ **Mistake** — `src/backend/Shared/Infrastructure/LinkDeletionMarkerStore.cs` (as of commit
`5637b49`'s parent — `GetAsync` shown, but `TryMarkOptimisticallyAsync`/`ConfirmAsync` had the
identical gap):

```csharp
var db = _connectionMultiplexer.GetDatabase();
// Still true after e9bf63d's partial fix: this comment used to claim "the resilience pipeline's
// own ExecuteAsync already races the call against this token" - wrong, this pipeline is
// circuit-breaker-only, no timeout strategy, so ExecuteAsync alone observes nothing.
var value = await _resilience.ExecuteAsync(
    ct => new ValueTask<RedisValue>(db.StringGetAsync(Key(hash))), cancellationToken);
return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<LinkDeletionMarker>((string)value!);
```

✅ **Do this instead** (`5637b49`) — race the call itself (not just the pipeline around it) against
the token with `Task.WaitAsync`, applied inside *every* `ExecuteAsync` lambda in the file, not just
the one that happened to get noticed first:

```csharp
// StackExchange.Redis's StringGetAsync has no CancellationToken overload at all (it isn't
// cancellable from the inside), so a caller's token - e.g. RedirectController's request
// budget - would otherwise be silently ignored here, no matter how slow a connected-but-hanging
// Redis gets. ExecuteAsync alone does NOT fix this: this pipeline (ResiliencePipelines.
// BuildCircuitBreaker) is circuit-breaker-only, no timeout strategy, so without an explicit
// .WaitAsync(ct) racing the call itself, ExecuteAsync just awaits the callback's task directly
// and never observes cancellation while it's still pending. Same fix needed in
// TryMarkOptimisticallyAsync/ConfirmAsync, which had the identical gap.
var value = await _resilience.ExecuteAsync(
    ct => new ValueTask<RedisValue>(db.StringGetAsync(Key(hash)).WaitAsync(ct)), cancellationToken);
```

`Task.WaitAsync` **abandons, not cancels**, the underlying call — this is fine for a read with no
side effect (`GetAsync`), and fine here for the two writes too since both are idempotent/TTL-bounded
(`TryMarkOptimisticallyAsync` is a conditional `SET ... NX`, `ConfirmAsync` just overwrites the same
key), but the same pattern must not be applied unthinkingly to a write whose completion matters even
after the caller stops waiting on it; abandoning a write-in-flight can leave its outcome unknown
rather than merely "not waited for."

**The reusable lesson, generalized beyond Redis:** a `ResiliencePipeline.ExecuteAsync` consumer
anywhere in this codebase should not be assumed to honor cancellation just because it compiles and
accepts a token — check whether a timeout strategy is actually part of *that specific* pipeline
before trusting it to race anything.

**Provenance:** the `GetAsync`-only instance found by `feature-review` on commit `6174662`, fixed in
`e9bf63d` with a regression test (a never-completing `TaskCompletionSource` simulating a
hung-but-connected Redis call). The wider scope (all three methods, plus the correction to the
`ExecuteAsync`-races-it-already claim) found during the external-review pass on commit `5637b49`
(see `.notes/review-reports/2026-10-30-1520-close-10-review-bugs.md`'s own narrative and
`.notes/feature-docs/2026-10-30-1524-external-review-10-bugs.md`, bug 1), fixed in the same commit
with two new regression tests mirroring the existing `GetAsync` one.

## Relatives

### Nodes

- [RedirectApi](node:redirect-api)
- [Redis](node:redis-master)

### Patterns

- [Caching](pattern:caching)
