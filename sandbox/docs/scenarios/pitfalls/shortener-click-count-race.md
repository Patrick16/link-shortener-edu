# Click-count increment raced once the consumer became scalable

**Category:** race-condition **Status:** fixed

`ShortenerService` maintains `Links.ClickCount` as a display counter (a separate, deliberate
consumer from `TrafficService`'s own `Clicks` audit table — see the [async messaging](pattern:async-messaging)
pattern doc for why both exist). The increment read the row, then wrote
`ClickCount + 1` back through EF's change tracker. The code even said why that was safe — at the
time: a comment claiming there was only ever one worker instance in this project's docker-compose.
That assumption became false the moment `shortener-service` gained the ability to scale to multiple
replicas (a later, unrelated commit), and the read-then-write pattern raced for real: two replicas
incrementing the same hot hash could both read the same starting value, and one increment would be
silently lost.

Found by scaling `shortener-service` 1→4 replicas under repeated-same-hash load and watching
throughput go *down*, not up (~190 msg/s → ~76-79 msg/s) — row-lock contention on the read-then-write
pattern, confirming this wasn't just a lost-update bug but also a performance regression under the
exact scaling this consumer was supposed to support.

🐛 **Bug** — read, then write through the change tracker:

```csharp
// Link is an immutable record everywhere else in the codebase; going through the change
// tracker's property accessor keeps this consistent with it and with the InMemory provider the
// tests use. A single worker instance in this project's docker-compose, so the read-then-write
// race this costs is negligible in practice.
var link = await context.Links.FirstOrDefaultAsync(x => x.Hash == @event.Hash, cancellationToken);
if (link is null) return;

context.Entry(link).Property(x => x.ClickCount).CurrentValue = link.ClickCount + 1;
await context.SaveChangesAsync(cancellationToken);
```

✅ **Fix** — a single atomic SQL `UPDATE`, no read step to race on:

```csharp
var updated = await context.Links
    .Where(x => x.Hash == @event.Hash)
    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ClickCount, x => x.ClickCount + 1), cancellationToken);

if (updated == 0)
{
    // LinkCreatedEvent for this hash may not have been consumed yet, or the hash is unknown.
    return;
}
```

This needed a test-infrastructure change too: EF Core's InMemory provider (what the existing tests
used) doesn't support `ExecuteUpdate` at all — the test fixture moved to a real SQLite in-memory
connection instead.

(fixed in `86c760c`, "optimize ClickTrackedConsumer for atomic click count updates" — see
`.notes/PLAN.md`, the 2026-09-25 "10k RPS load-testing pass" entry, Bug #3, for the full
before/after throughput numbers)

## Relatives

### Nodes

- [ShortenerService](node:shortener-service)

### Patterns

- [Async messaging](pattern:async-messaging)
