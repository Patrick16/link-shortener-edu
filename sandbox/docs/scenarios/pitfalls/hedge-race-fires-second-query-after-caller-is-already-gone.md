# A hedge race kept firing its second branch after the caller had already disconnected

**Category:** performance **Status:** fixed

A hedge/race helper that takes the caller's own cancellation token should check it before paying
for the expensive branch, not just thread it through for the Mongo driver to eventually notice. The
original code only asked "did the hedge delay elapse, or did `first` fail?" before starting
`second` — it never asked "is anyone still waiting for either answer?" An ASP.NET Core action's
`CancellationToken` is wired to `HttpContext.RequestAborted`; if the client disconnects mid-request
during exactly the window the hedge delay is timed for, the helper doubles Mongo query load for a
response nobody will ever read. Low severity on its own (the wasted query still gets cancelled once
the driver notices), but avoidable with one check, and hedging exists specifically to add load only
when it's worth it.

⚠️ **Mistake** — `src/backend/Shared/Infrastructure/HedgedClickMetaQueryService.cs` (before commit
`8a654f1`):

```csharp
var completedEarly = await Task.WhenAny(firstTask, delayTask);
if (completedEarly == firstTask && firstTask.IsCompletedSuccessfully)
{
    return await firstTask;
}

// Falls straight through to starting `second` - no check for whether the caller
// (cancellationToken) is even still around to read the result.
var secondTask = second(ctsSecond.Token);
```

✅ **Do this instead** (commit `8a654f1`, disposal timing corrected in `11fb360`) — short-circuit on
the caller's own token before paying for a second query:

```csharp
var completedEarly = await Task.WhenAny(firstTask, delayTask);
if (completedEarly == firstTask && firstTask.IsCompletedSuccessfully)
{
    ctsFirst.Dispose();
    return await firstTask;
}

if (cancellationToken.IsCancellationRequested)
{
    // The caller itself gave up in this same window - no point firing a second query nobody
    // will ever read the result of. Let cancellation surface through firstTask's own linked
    // token instead.
    _ = firstTask.ContinueWith(_ => ctsFirst.Dispose(), TaskScheduler.Default);
    return await firstTask;
}

var secondTask = second(ctsSecond.Token);
```

The `ctsFirst.Dispose()` placement in this snippet is its own separate, already-documented
lesson — see [Disposing a CancellationTokenSource before its task finishes](pitfall:cts-disposed-before-task-finishes).

(see `.notes/review-reports/2026-10-29-2235-hedged-mongo-reads.md`, F3 — fixed in `8a654f1`)

## Relatives

### Nodes

- [ReportingApi](node:reporting-api)
- [Mongo](node:mongo1)

### Patterns

- [Hedged reads](pattern:hedged-reads)
