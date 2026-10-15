## What it is

Letting a logged-in user choose their own short code ("alias") instead of accepting a randomly
generated one — and, since that breaks a quiet assumption the rest of the system relied on, what
has to change about *how* a link gets created to make that safe.

## What it solves

A random hash from `Sha256Base62HashGenerator` is practically guaranteed not to collide with
anything (~2.2×10^14 keyspace) — see [LinkApi](node:link-api) — so the plain creation path never
checks for a collision before handing one back. A user-chosen alias is the opposite: popular short
words collide constantly, and the entire point of choosing one is getting a specific, predictable
value. That needs a real uniqueness guarantee the random-hash path never had to provide.

## How it works

1. `POST /Links` with an optional `alias` field — only reachable by an authenticated caller (`401`
   otherwise). Normalized to lowercase and validated against a charset/length pattern and a small
   reserved-word list (`scalar`, `health`, `openapi`, `api`, `admin`, `links` — real single-segment
   routes this stack already uses, not a guessed list) before anything else happens.
2. Unlike the plain-hash path (see [Async messaging](pattern:async-messaging), which never blocks
   on Postgres), alias creation calls [ShortenerService](node:shortener-service) **synchronously**
   over the same gRPC transport the `Messaging:Mode=grpc` toggle already uses — but unconditionally,
   independent of what that global toggle is set to. ShortenerService runs the exact same
   `LinkCreatedConsumer.HandleBatchAsync` batch handler either way (a batch of one, here), so there
   is one persistence code path for both random hashes and aliases, just two different ways of
   reaching it.
3. Postgres's unique constraint on `Hash` is the only arbiter of "is this alias free" — no
   client-side cache, no reservation. Two callers racing for the same alias both reach the same
   insert; one wins, the other's insert fails with a unique-key violation, which
   `HandleBatchAsync` now reports back (see below) instead of silently discarding.
4. The caller gets a real answer before the HTTP response returns: success if the alias was free,
   `409 Alias taken` if it lost the race. Never "created, maybe."

### Why a conflict could no longer be silently dropped

`HandleBatchAsync` pre-dates aliases — its redelivery-safety logic (skip an insert if the hash is
already stored) was written for a world where "same hash, different attempt" could only mean a
RabbitMQ redelivery or a losing replica re-inserting its own copy of the *same* message, never two
genuinely different links. Aliases break that assumption: a losing racer's row is a **different**
link (a different `OriginalLink`), not a duplicate of the winner's. The fix compares the existing
row's content, not just its presence — matching content is still silently skipped (a real
redelivery, unchanged behavior); a mismatch is now reported as a poison message instead
(`BatchOutcome.WithPoison`, which now carries a reason string per message, not just a bare id). The
gRPC path turns that into `RpcException(AlreadyExists)` → `409`; the RabbitMQ path (not reachable
for aliases today, since they're always synchronous, but sharing the same handler) would
dead-letter with the same reason in its `x-dead-reason` header. The comparison applies uniformly to
random hashes too — a genuine hash collision there is astronomically unlikely, but it's no longer
silently discarded on the rare chance it happens; the reason text says which kind of conflict it
was (`existing.HasCustomAlias` — persisted on `Link` specifically so the *existing*, winning row can
answer that question, since by the time a conflict is detected, the losing message is all the
consumer has to go on for the loser's side).

### Why this doesn't need an application-level lock

Postgres's unique constraint on `Hash` is the only thing enforcing uniqueness — no Redis claim, no
`SELECT ... FOR UPDATE`, no new transaction scope. The existing `catch (DbUpdateException)` recovery
in `HandleBatchAsync` (originally written for redelivery/competing-consumer races — ShortenerService
is one of the services `control-api` can scale to N replicas) already re-queries Postgres after a
conflict; by the time that query runs, the winning row is guaranteed already committed (a
unique-violation only fires once the conflicting transaction has committed — otherwise the insert
just blocks waiting for it), so there's no race on the re-check itself, and no new locking needed on
top of what already existed. Verified live against the real running stack, not just unit tests: 10
concurrent requests for the same alias produced exactly one success and nine `409`s, with exactly
one row in Postgres.

### Why synchronous gRPC, not an async claim-based approach

An async design was designed in detail before this one and deliberately not built — worth recording
both, since this is exactly the kind of trade-off this project exists to make visible.

**The rejected design:** `LinkApi` takes a Redis claim (`SET NX alias:claim:{name}`, a TTL as the
only crash-recovery backstop) before publishing a `LinkCreatedEvent` to RabbitMQ as usual, the same
way a random hash already does. A 7-day read-cache, populated after the consumer's real persist (and
refreshed on any positive existence check, not just after a persist), would let repeat lookups of a
popular/contested name skip Postgres. The SQLite fallback-queue worker (the one that already retries
a stuck publish every 30s during a broker outage) would renew the claim on every retry tick, so a
long broker outage wouldn't let the claim expire while the message was still in flight — with a
`pending`/`published` marker and a reconciliation pass on `LinkApi` startup to clean up orphaned
claims from a crash between claiming and publishing. The consumer-side conflict handling (the
content-comparison fix above) is identical either way — it isn't specific to the chosen transport.

**Why it loses:** it works for anonymous callers (this project ended up requiring login for aliases
anyway, closing that advantage) and never blocks the HTTP response on ShortenerService's
availability — but the cost is a second, hand-built consistency mechanism layered on top of
Postgres's own constraint, not a replacement for it (the DB is still the final arbiter either way).
Every piece of that mechanism — claim TTL, renewal, the `pending`/`published` handshake, startup
reconciliation — exists only to approximate, imperfectly, a guarantee the synchronous path gets for
free from one RPC call. The approximation has a real gap, too: if `LinkApi` loses the claim before
the message is actually persisted (Redis restarting mid-flight, or a TTL shorter than how long the
message was genuinely stuck) and a second request grabs the same name in that window, the race
happens in seconds, not hours — the renewal story only helps with the *broker*-outage case, not a
*Redis*-outage one, and nothing closes that second window without adding yet another layer.

**What's common to both:** the strong guarantee (a definite yes/no before the HTTP response returns)
can only come from a synchronous round trip to wherever uniqueness is actually enforced — Postgres.
An async design can make that round trip fast to *not* need in the common case, and can serve
anonymous callers, but can't make the guarantee itself cheaper, only hide where it's paid. For this
project, with aliases already gated to logged-in users and ShortenerService's gRPC endpoint already
always listening regardless of `Messaging:Mode`, that round trip was cheap enough to just take
directly.

## How it's implemented here

```csharp
// src/backend/Services/LinkApi/Controllers/LinksController.cs, CreateWithAliasAsync
var aliasEvent = new LinkCreatedEvent
{
    Hash = alias, OriginalLink = originalLink, ShortenLink = alias,
    CreatedAt = createdAt, UserId = userId, HasCustomAlias = true,
};

var result = await _aliasCreator.CreateAsync(aliasEvent, cancellationToken);
if (result == AliasCreationResult.AliasTaken)
{
    return Problem(detail: $"Alias '{alias}' is already taken.", statusCode: 409, title: "Alias taken.");
}
```

```csharp
// src/backend/Shared/Infrastructure/CustomAliasCreator.cs
public async Task<AliasCreationResult> CreateAsync(LinkCreatedEvent linkCreatedEvent, CancellationToken cancellationToken)
{
    var request = new PublishRequest { MessageId = Guid.NewGuid().ToString(), Topic = Topics.LinkCreated, Payload = JsonSerializer.Serialize(linkCreatedEvent) };
    try
    {
        await client.PublishAsync(request, cancellationToken: cancellationToken); // same RPC the messaging-mode toggle uses
        return AliasCreationResult.Created;
    }
    catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
    {
        return AliasCreationResult.AliasTaken;
    }
}
```

```csharp
// src/backend/Services/ShortenerService/LinkCreatedConsumer.cs, the content-comparison fix
if (existing.OriginalLink == item.Payload.OriginalLink)
{
    continue; // same link, already persisted - redelivery or a losing racing replica, not an error
}

poisonMessages.Add(new PoisonMessage(item.MessageId, ConflictReason(item.Payload.Hash, existing)));
```

`GrpcAliasCreator`/`ICustomAliasCreator`'s DI registration (`AddAliasGrpcClient`,
`Shared/Infrastructure/GrpcMessagingExtensions.cs`) is deliberately separate from the standing
`AddEventDispatcher` — `RedirectApi` also calls `AddEventDispatcher` and has no use for an alias
client, so the gRPC channel this feature needs is registered only in `LinkApi`'s own composition
root, not folded into shared wiring every `IEventDispatcher` consumer would otherwise pay for.

## Relatives

### Nodes

- [LinkApi](node:link-api) — validates, normalizes, and makes the synchronous call
- [ShortenerService](node:shortener-service) — the one persistence path both random hashes and
  aliases go through

### Patterns

- [Async messaging](pattern:async-messaging) — the transport this reuses, but bypasses the mode
  selection of
