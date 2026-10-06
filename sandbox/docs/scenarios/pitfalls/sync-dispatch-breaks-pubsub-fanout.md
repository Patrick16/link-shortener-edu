# Point-to-point RPC dispatch silently breaks pub/sub fan-out to the other consumers

**Category:** architecture-bug **Status:** known limitation

Watch for this any time an event-driven system grows an alternate, point-to-point transport behind
the same abstraction a pub/sub transport used to be the only implementation of. A topic exchange
delivers one message to *every* bound queue for free — that's the whole value of fan-out. A direct
RPC call to one specific service does not: it reaches exactly the target it was given, and nothing
else that used to also be listening on the same event keeps receiving it. Swapping the transport
behind an interface like `IEventDispatcher`/`IMessagePublisher` can look like a drop-in
replacement at the call site (same method signature, same event type) while silently changing the
delivery topology from "everyone who cares" to "the one thing I called" — a behavior change that
doesn't show up in any type signature or compiler warning.

**Concrete instance in this project:** `RedirectApi`'s `ClickTrackedEvent` is normally published to
a RabbitMQ topic exchange with three independent fan-out consumers: `TrafficService` (the audit
trail), `ShortenerService` (the `Links.ClickCount` display counter), and `ReportingService` (the
ClickHouse CQRS read side). `messaging-mode=grpc`'s `SyncGrpcDispatcher` calls `TrafficService`
directly over gRPC and stops there — nothing is ever published to RabbitMQ in this mode, so
`ShortenerService`'s and `ReportingService`'s consumers never receive the event at all. While gRPC
mode is active, the "my links" dashboard's click count and the ClickHouse-backed reports both
silently freeze, even though redirects keep succeeding and `TrafficService`'s own Postgres/Mongo
writes are unaffected — the kind of gap that's easy to not notice precisely because the request
path itself looks completely healthy.

This was only half-documented at first — an earlier pass caught and documented the
`ReportingService` half of this gap, but the identical `ShortenerService` half went unnoticed until
a later review pass found it. Building true multi-target gRPC fan-out (`RedirectApi` calling two
or three separate gRPC targets per click) was out of scope for either pass — this is a structural
trade-off of point-to-point RPC vs. pub/sub fan-out, not a bug with a narrow code fix. What
actually changed is that the gap is now explicitly called out in `RedirectController.cs`'s own
comment and in `architecture.json`'s edge notes, instead of being a silent, only-partially-known
gap.

(see `.notes/review-reports/2026-10-06-1430-messaging-toggle-grpc-review.md`, F3 — the source
report marks this "fixed" in the sense that the gap is now documented everywhere it should be, but
no architectural change closes it; written up here as a known limitation rather than a code
before/after, since there is no code fix to show)

## Relatives

### Nodes

- [RedirectApi](node:redirect-api) — the dispatch point where the fan-out silently narrows
- [ShortenerService](node:shortener-service) — click-count consumer that goes stale in this mode
- [TrafficService](node:traffic-service) — the one consumer gRPC mode actually reaches

### Patterns

- [Async messaging](pattern:async-messaging) — the fan-out semantics this mode doesn't preserve
