## What it is

An ASP.NET Core Web API — one of five .NET services in this project. Accepts link-creation
requests and serves reads (single-hash lookup and "my links" listing), plus authenticated-owner
deletion.

## What it solves

A short-link service needs to generate a hash and hand it back to the caller *fast* — but
persisting that link and everything downstream of it (the audit trail, eventual read scaling) can
happen a beat later. Making the caller wait for a database write on the hot creation path doesn't
buy anything the caller can observe, so LinkApi doesn't do one.

## How it works

`POST /Links` generates the hash itself (no database round trip needed for that) and returns it to
the caller *synchronously* — persistence happens asynchronously afterward. See
[Async messaging](pattern:async-messaging) for the mechanism.

`GET /Links/{hash}` is a cache-aside read — see [Caching](pattern:caching).

## How it's implemented here

```csharp
// src/backend/Services/LinkApi/Controllers/LinksController.cs:34
public async Task<ActionResult<LinkResponse>> CreateLink(
    [FromBody] LinkCreateRequest request, CancellationToken cancellationToken)
{
    var hash = _hashGenerator.Generate(request.OriginalLink);
    var linkCreatedEvent = new LinkCreatedEvent { Hash = hash, OriginalLink = request.OriginalLink, /* ... */ };
    await _dispatcher.DispatchAsync(linkCreatedEvent, Topics.LinkCreated, cancellationToken);

    // Optimistic cache write, short TTL - closes the PgCat read-your-writes race for an
    // immediate redirect. See this node's Pitfalls for why.
    await _service.CacheAsync(new Link(hash, request.OriginalLink, hash, createdAt, userId),
        hash, cancellationToken, OptimisticCacheTtlSeconds);

    return new LinkResponse(hash, createdAt); // returns before ShortenerService has persisted anything
}
```

The hash comes from `IHashGenerator` (`Sha256Base62HashGenerator` — SHA-256 of the original URL,
Base62-encoded, truncated), a pure function with no database dependency, which is what makes the
synchronous-return/async-persist split possible in the first place.

`DispatchAsync` goes through `IEventDispatcher`, not a direct publisher call — see
[Async messaging](pattern:async-messaging) for the RabbitMQ/gRPC transport toggle this abstracts
over. The cache write only happens after it succeeds, not before (see this node's Pitfalls).

A Bearer token is read *optionally* — `CreateLink` has no `[Authorize]`, so anonymous callers work
identically to authenticated ones; the only difference is whether `Link.UserId` ends up populated
from the token's `sub` claim. `GetLinks` (the "my links" listing) is the opposite: it requires
either a valid Bearer token or control-api's internal API key scheme, and always filters by caller.

**Custom aliases are the one exception to "always synchronous-return, async-persist."** When
`LinkCreateRequest.Alias` is set (authenticated callers only — `401` otherwise), `CreateLink`
branches to a different path that calls ShortenerService synchronously over gRPC and only returns
once the row is confirmed persisted or the alias is confirmed taken — see
[Custom aliases](pattern:custom-aliases) for why a real uniqueness guarantee needs that trade-off
even though the plain-hash path deliberately never makes it.

**`DELETE /Links/{hash}` is owner-only and publishes through the same `IEventDispatcher` as
`CreateLink`** - no forced transport the way custom aliases force sync gRPC; delete respects
whichever messaging mode is active. Anonymous links (`UserId == null`) can never be deleted by
anyone, and a caller who isn't the link's own owner gets `403`, not `404` - the hash is already in
their hands (they're asking to delete it), so there's nothing extra to protect by pretending it
doesn't exist. An already-deleted hash returns `204` idempotently instead of erroring. Before
publishing, the handler writes an optimistic "deleted" marker to a *separate* Redis key
(`link:deleted:{hash}`, not the main `link:{hash}` cache entry) via `ILinkDeletionMarkerStore` -
see [RedirectApi](node:redirect-api) for why that key is checked first on every resolve, and
[ShortenerService](node:shortener-service) for how the marker gets confirmed. `CreateLink` also
accepts an optional `TtlSeconds`, converted once to an absolute `ExpiresAt` and carried on
`LinkCreatedEvent` for both the plain-hash and custom-alias paths - see ShortenerService's
`LinkExpiryWorker` for what actually acts on it. `GetLinks` filters out deleted and expired links
by default; there's no "show them anyway" toggle, since nothing in this feature calls for one.

## Pitfalls

- ⚠️ [RabbitMQ's publisher stack started even when the active transport doesn't use it](pitfall:rabbitmq-publisher-started-regardless-of-messaging-mode)
  (performance) — fixed
- 🐛 [A read immediately after a write can land on a lagging replica](pitfall:pgcat-read-your-writes-race)
  (race-condition) — fixed; this node writes the optimistic cache entry that closes it
- 🐛 [Three services ran identical caching code against three disjoint Redis keyspaces](pitfall:redis-instance-name-breaks-cross-service-cache-sharing)
  (coupling) — fixed; found while verifying the fix above

## Relatives

### Nodes

- [RedirectApi](node:redirect-api) — resolves the hashes this node creates
- [ShortenerService](node:shortener-service) — persists what this node publishes
- [Redis](node:redis-master) — backs `GetLink`'s cache
- [RabbitMQ](node:rabbitmq) — carries `LinkCreatedEvent` to ShortenerService

### Patterns

- [Async messaging](pattern:async-messaging)
- [Caching](pattern:caching)
- [Custom aliases](pattern:custom-aliases)
