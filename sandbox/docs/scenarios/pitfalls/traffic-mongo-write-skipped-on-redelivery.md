# Mongo ClickMeta write was permanently skipped after a Postgres-committed redelivery

**Category:** resilience-gap **Status:** fixed

Watch for a redelivery-safety check that only looks at *one* of several non-transactional writes a
handler makes for the same logical event. The Postgres `Click` row and the Mongo `ClickMeta`
document are two separate, non-transactional writes for the same click — see
[Async messaging](pattern:async-messaging) for why redelivery safety matters here at all. Gating
the *whole* handler on whether just the Postgres side is already done — "if a click's `Id` is
already stored there, return immediately" — silently assumes every write in the handler always
succeeds or fails together, which at-least-once delivery semantics never actually guarantee: a
handler can partially succeed (Postgres commits, Mongo throws) and get redelivered specifically
*because* of the write that's still missing, only for the redelivery-safety check to skip
re-attempting exactly that write.

⚠️ **Mistake** — one early return skips both writes, not just the one that actually succeeded:

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

✅ **Do this instead** — only the Postgres insert is conditional on `alreadyStored`; the Mongo
write always runs, on every delivery:

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

## Relatives

### Nodes

- [TrafficService](node:traffic-service)

### Patterns

- [Async messaging](pattern:async-messaging)
