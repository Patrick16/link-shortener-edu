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

## Relatives

### Nodes

- [RedirectApi](node:redirect-api) — records the click after dispatch and must not fail the redirect
- [ReportingApi](node:reporting-api) — `RedisTopLinksReader` had the identical catch-too-narrow bug
- [Redis](node:redis-master) — the dependency whose timeouts escaped the catch

### Patterns

- [Caching](pattern:caching) — same rule: a Redis failure degrades, it never propagates as an error
