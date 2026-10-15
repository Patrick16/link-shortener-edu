# A deletion tombstone written before the delete event's dispatch is confirmed can outlive a delete that never happened

**Category:** resilience-gap
**Status:** fixed

When an operation writes two things that *look* independent — a fast local side effect, and a
dispatch to whatever actually makes the operation real — the order still matters, even if the
local write "can't really fail." The side effect should never imply success for something that
hasn't actually happened yet. This project already had the correct version of this rule written
down for creation (`CreateLink`'s optimistic cache write runs only after its event dispatch
succeeds, specifically so a failed dispatch under sync/gRPC mode never leaves a cache entry
implying a link exists that was never actually created) — and then its own mirror operation,
delete, got built the wrong way round before review caught it.

**Concrete instance in this project:** `LinksController.DeleteLink` wrote the optimistic Redis
"deleted" tombstone (`link:deleted:{hash}`, a separate key from the main cache-aside entry, checked
first on every `RedirectApi` resolve) *before* dispatching `LinkDeletedEvent`. In the default
async/RabbitMQ mode this is nearly invisible, since `AsyncQueueDispatcher` essentially never throws
— it just enqueues. But `Messaging:Mode=grpc` is a fully supported, independently toggled mode of
this exact system, and its `SyncGrpcDispatcher` has no fallback queue underneath it: a transient
failure reaching `ShortenerService` throws straight through. By the time that throw happened, the
tombstone write on the line above had already succeeded. The caller correctly saw a 502/503 — the
delete request genuinely failed — but every other visitor of that link got a real 410 Gone page for
up to the tombstone's full 60-second TTL, for a link that was never actually deleted.

⚠️ **Mistake** — `src/backend/Services/LinkApi/Controllers/LinksController.cs` (`DeleteLink`, before
`437bd19`):
```csharp
// Always publishes, regardless of whether this wins or loses the SETNX race below (a
// concurrent duplicate DELETE for the same hash) - simpler than branching on the result,
// and redundant delivery is exactly what LinkDeletedConsumer is built to tolerate.
await _deletionMarkers.TryMarkOptimisticallyAsync(hash, DeletedReason.Manual, cancellationToken);
await _dispatcher.DispatchAsync(new LinkDeletedEvent { Hash = hash }, Topics.LinkDeleted, cancellationToken);
```

✅ **Do this instead** — dispatch first, mark the tombstone only once that succeeds, mirroring
`CreateLink`'s own ordering in the opposite direction:
```csharp
// Dispatch first, mark the optimistic tombstone only after it succeeds - mirrors CreateLink's
// own ordering above and for the same reason, mirrored in the opposite direction: a failed
// dispatch (e.g. SyncGrpcDispatcher in gRPC mode, which has no fallback queue and throws
// straight through to a 502/503 here) must not leave the tombstone behind regardless.
await _dispatcher.DispatchAsync(new LinkDeletedEvent { Hash = hash }, Topics.LinkDeleted, cancellationToken);
// Always published, regardless of whether this wins or loses the SETNX race below (a
// concurrent duplicate DELETE for the same hash) - simpler than branching on the result,
// and redundant delivery is exactly what LinkDeletedConsumer is built to tolerate.
await _deletionMarkers.TryMarkOptimisticallyAsync(hash, DeletedReason.Manual, cancellationToken);
```

Harmless in the default async mode for the same reason the sibling click-counter-ordering mistake
is — a dispatcher that essentially never throws hides the bug completely. It only starts mattering
once a dispatcher implementation exists that can genuinely fail on the request's own call stack,
which is exactly what the gRPC messaging-mode toggle introduced.

(see `.notes/review-reports/2026-10-15-1437-link-deletion-and-expiration.md`, F1 — fixed in `437bd19`)

## Relatives

### Nodes

- [LinkApi](node:link-api) — where the ordering bug lived
- [RedirectApi](node:redirect-api) — reads the tombstone this bug could falsely set
- [ShortenerService](node:shortener-service) — the `Messaging:Mode=grpc` dispatch target whose
  failure mode exposed this

### Patterns

- [Async messaging](pattern:async-messaging) — the dispatcher abstraction (`IEventDispatcher`) that
  made this failure mode reachable once a synchronous, throwing implementation existed
