# CreateLink was a hard-coded stub that persisted nothing

**Category:** logic-bug **Status:** fixed

Early on, `POST /Links` null-checked the request and then unconditionally returned a fabricated
response — the literal string `"shorten"` as the hash, unrelated to the request's actual URL, with
no database write, no cache population, and no way to later resolve what was just "created". Any
client got back a response claiming success; a subsequent `GET /Links/shorten` (or any hash) 404'd
since nothing was ever persisted.

🐛 **Bug** — `LinksController.CreateLink`:

```csharp
public LinkResponse CreateLink([FromBody] LinkCreateRequest request)
{
    ArgumentNullException.ThrowIfNull(request);
    return new LinkResponse("shorten", DateTime.UtcNow);
}
```

✅ **Fix** — generates a real hash and publishes it for `ShortenerService` to persist, instead of
faking a response:

```csharp
public async Task<ActionResult<LinkResponse>> CreateLink(
    [FromBody] LinkCreateRequest request,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);

    var hash = _hashGenerator.Generate(request.OriginalLink);
    var linkCreatedEvent = new LinkCreatedEvent { Hash = hash, OriginalLink = request.OriginalLink, /* ... */ };
    await _publisher.PublishAsync(linkCreatedEvent, Topics.LinkCreated, cancellationToken);

    return new LinkResponse(hash, createdAt);
}
```

(see review-reports/2026-09-17-0153-3674b1b-linkapi-dbcontext-redis.md, F2 — fixed same day in
`22bcacf`)

## Relatives

### Nodes

- [LinkApi](node:link-api)

### Patterns

- [Async messaging](pattern:async-messaging)
