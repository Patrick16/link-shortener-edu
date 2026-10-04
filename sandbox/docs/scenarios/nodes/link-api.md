## What it is

An ASP.NET Core Web API — one of five .NET services in this project. Accepts link-creation
requests and serves reads (single-hash lookup and "my links" listing).

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
    await _publisher.PublishAsync(linkCreatedEvent, Topics.LinkCreated, cancellationToken);
    return new LinkResponse(hash, createdAt); // returns before ShortenerService has persisted anything
}
```

The hash comes from `IHashGenerator` (`Sha256Base62HashGenerator` — SHA-256 of the original URL,
Base62-encoded, truncated), a pure function with no database dependency, which is what makes the
synchronous-return/async-persist split possible in the first place.

A Bearer token is read *optionally* — `CreateLink` has no `[Authorize]`, so anonymous callers work
identically to authenticated ones; the only difference is whether `Link.UserId` ends up populated
from the token's `sub` claim. `GetLinks` (the "my links" listing) is the opposite: it requires
either a valid Bearer token or control-api's internal API key scheme, and always filters by caller.

## Pitfalls

None yet.

## Relatives

### Nodes

- [RedirectApi](node:redirect-api) — resolves the hashes this node creates
- [ShortenerService](node:shortener-service) — persists what this node publishes
- [Redis](node:redis-master) — backs `GetLink`'s cache
- [RabbitMQ](node:rabbitmq) — carries `LinkCreatedEvent` to ShortenerService

### Patterns

- [Async messaging](pattern:async-messaging)
- [Caching](pattern:caching)
