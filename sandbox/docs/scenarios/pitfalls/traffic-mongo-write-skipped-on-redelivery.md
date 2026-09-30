# Mongo ClickMeta write was permanently skipped after a Postgres-committed redelivery

**Category:** logic-bug **Status:** fixed

The Postgres `Click` row and the Mongo `ClickMeta` document are two separate, non-transactional
writes for the same click — see [Async messaging](pattern:async-messaging) for why redelivery
safety matters here at all. The redelivery-safety check only looked at Postgres: if a click's `Id`
was already stored there, the handler returned immediately — never attempting the Mongo write
again, even if that was the *reason* the message got redelivered in the first place.

🐛 **Bug** — one early return skips both writes, not just the one that actually succeeded:

```csharp
internal async Task HandleAsync(ClickTrackedEvent @event, CancellationToken cancellationToken)
{
    var alreadyStored = await context.Clicks.AnyAsync(x => x.Id == @event.Id, cancellationToken);
    if (alreadyStored)
    {
        // Mongo isn't touched again either in that case.
        return;
    }

    context.Clicks.Add(new Click(@event.Id, /* ... */));
    await context.SaveChangesAsync(cancellationToken);

    var geo = await _geoIpResolver.ResolveAsync(@event.IpAddress, cancellationToken);
    await _clickMetaStore.SaveAsync(new ClickMeta(@event.Id, /* ... */), cancellationToken);
}
```

If `_clickMetaStore.SaveAsync` ever threw (a transient Mongo outage), the message got nacked and
requeued — but by the time it was redelivered, the Postgres row from the *first* attempt already
existed, so `alreadyStored` was `true` and the handler returned before ever retrying the Mongo
write. The click permanently had no `ClickMeta` document, acked as if fully processed, no error or
log signal beyond the original transient warning.

✅ **Fix** — only the Postgres insert is conditional on `alreadyStored`; the Mongo write always
runs, on every delivery:

```csharp
internal async Task HandleAsync(ClickTrackedEvent @event, CancellationToken cancellationToken)
{
    var alreadyStored = await context.Clicks.AnyAsync(x => x.Id == @event.Id, cancellationToken);
    if (!alreadyStored)
    {
        context.Clicks.Add(new Click(@event.Id, /* ... */));
        await context.SaveChangesAsync(cancellationToken);
    }

    // Runs unconditionally, redelivered or not - safe because it's an upsert by Id, so retrying
    // it just re-applies the same document instead of duplicating anything.
    var geo = await _geoIpResolver.ResolveAsync(@event.IpAddress, cancellationToken);
    await _clickMetaStore.SaveAsync(new ClickMeta(@event.Id, /* ... */), cancellationToken);
}
```

This is safe specifically *because* the Mongo write is already idempotent (upsert by `Id`) —
re-running it on a redelivery re-applies the same document instead of duplicating anything. The
fix predates the later batch-processing refactor (`HandleBatchAsync` today), but the same principle
carries through: the current code still attempts the Mongo write for every click in a batch, even
ones already present in Postgres.

(see review-reports/2026-09-22-1339-b25f4bf-click-tracking-user-links.md, F1)

## Relatives

### Nodes

- [TrafficService](node:traffic-service)

### Patterns

- [Async messaging](pattern:async-messaging)
