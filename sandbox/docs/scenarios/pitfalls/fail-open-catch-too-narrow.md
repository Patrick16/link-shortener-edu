# Fail-open that only caught RedisException let timeouts fail the redirect

**Category:** resilience-gap **Status:** fixed

A side effect on a hot request path (here: recording a click) has to fail open, so a problem in that side
effect cannot fail the request. The catch has to match the failures the client can actually throw. Catching
the client's own base exception type is not enough, because timeouts can come out as a different type.

**Concrete instance in this project:** RedirectApi records each owned click in Redis after the click has
already been dispatched. The redirect itself must not fail because of that write. The code caught only
`RedisException`, but StackExchange.Redis raises `RedisTimeoutException`, which derives from
`System.TimeoutException` and not from `RedisException`. A slow Redis therefore escaped the catch after
the click was dispatched, so the redirect would return a 500 even though its real work had already
succeeded. (By code reading; a live timeout was not induced.)

The base type was checked against the referenced StackExchange.Redis 2.12.14 assembly:
`RedisTimeoutException : TimeoutException`, `RedisConnectionException : RedisException`.

Mistake:

```csharp
// RedisClickCounterService.RecordClickAsync and TopLinksReader.GetTopAsync (bd47119)
catch (RedisException ex)
{
    _logger.LogWarning(ex, "...");
}
```

Do this instead (a2b405e, `src/backend/Shared/Infrastructure/RedisClickCounterService.cs:40` and
`TopLinksReader.cs:44`):

```csharp
catch (Exception ex) when (ex is not OperationCanceledException)
{
    // Any Redis failure, including timeouts. Cancellation must still propagate, so a client that
    // disconnects or a host that shuts down still stops the work.
    _logger.LogWarning(ex, "Failed to record click for hash {Hash} in the top-links counter", hash);
}
```

The `OperationCanceledException` exclusion matters: a broad catch would otherwise also swallow the
cancellation that a shutting-down host or an aborted request relies on.

**Provenance:** before and after are real commits (bd47119 and a2b405e).
**Not reproduced:** no live timeout was induced. The type hierarchy was checked by reflection, and the
behaviour was verified by reading the code.

## Second concrete instance: a generic catch swallowing cancellation, not just the wrong exception type

The same underlying mistake — a catch filter that doesn't match every exception type a dependency
can actually throw — recurs with a different exception and a different call site once a per-request
deadline enters the picture. `EntityCacheService.GetCachedAsync`/`IsNegativelyCachedAsync` both had
a bare `catch (Exception ex)` around their Redis reads, added for the ordinary fail-open case (a
real Redis error should degrade to "treat as a cache miss," never fail the request). Once
[RedirectApi](node:redirect-api) gained a per-request budget, that same broad catch also caught the
`OperationCanceledException` the budget raises when it trips
— logging a misleading "failed to read cache" warning and returning `null`/`false` as if it were a
normal fail-open miss, instead of letting the cancellation propagate so the controller could shed
the request cleanly with a `503`.

⚠️ **Mistake** — `src/backend/Shared/Common/EntityCacheService.cs` (commit `6174662`):

```csharp
catch (Exception ex)
{
    _logger.LogWarning(ex, "Failed to read cache for {Key} - treating as a cache miss", Key(id));
    return null;
}
```

✅ **Do this instead** (`e9bf63d`) — exclude `OperationCanceledException`, the same shape this
file's first instance above already used:

```csharp
catch (Exception ex) when (ex is not OperationCanceledException)
{
    _logger.LogWarning(ex, "Failed to read cache for {Key} - treating as a cache miss", Key(id));
    return null;
}
```

`CacheAsync`/`InvalidateCacheAsync` in the same file deliberately still catch broadly — they're
pure best-effort writes with no caller waiting on a cancellation signal to matter, so there's
nothing for them to propagate cancellation *to*. The fix only applies to the two read paths a
caller can be actively waiting on under a deadline.

**Provenance:** found by `feature-review` on commit `6174662`, fixed in `e9bf63d`.

## Relatives

### Nodes

- [RedirectApi](node:redirect-api) — records the click after dispatch and must not fail the redirect;
  also the source of the second instance's per-request budget
- [ReportingApi](node:reporting-api) — `RedisTopLinksReader` had the identical catch-too-narrow bug
- [Redis](node:redis-master) — the dependency whose timeouts escaped the catch

### Patterns

- [Caching](pattern:caching) — same rule: a Redis failure degrades, it never propagates as an error
