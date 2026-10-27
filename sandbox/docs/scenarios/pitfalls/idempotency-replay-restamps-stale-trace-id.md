# A byte-for-byte idempotency replay can carry a stale traceId into a fresh request

**Category:** resilience-gap **Status:** fixed

Watch for this any time an idempotency layer replays a *stored* response verbatim instead of
re-deriving it. Fidelity to the original response is the whole point of a replay — same status
code, same body — but one field is deliberately request-scoped, not part of "the outcome": a
correlation id meant to point whoever's reading it at the trace that actually produced the
response in front of them. A naive byte-for-byte replay carries the *first* request's value
forward forever, which is correct for every other field and wrong for exactly this one.

**Concrete instance in this project:** `ProblemDetails.Extensions["traceId"]` is stamped at
`Problem()`-call time by a global `CustomizeProblemDetails` callback
(`ApiExceptionHandlingExtensions.cs`), using whatever `HttpContext.TraceIdentifier` was current for
*that* request. `LinksController`'s idempotency replay path originally deserialized the stored
`problem+json` body into an immutable `JsonElement` and returned it unmodified — so a replayed
`409`/`400` response carried the *original* attempt's `traceId`, not the replaying request's own.
Anyone debugging the replayed response by following its `traceId` into Jaeger/Loki/Grafana would
land on the wrong, unrelated trace — the first attempt's, not the one that actually produced the
response they're looking at.

⚠️ **Mistake** — `src/backend/Services/LinkApi/Controllers/LinksController.cs` (commit `05934eb`):

```csharp
// Replay the first caller's exact prior outcome - no new hash generated, nothing
// re-published to the bus.
return new ObjectResult(JsonSerializer.Deserialize<JsonElement>(existing.BodyJson!, ReplayJsonOptions))
{
    StatusCode = existing.StatusCode,
    ContentTypes = { existing.ContentType! },
};
```

✅ **Do this instead** (`05f0754`) — parse the stored body as a mutable `JsonNode` instead of an
immutable `JsonElement` specifically for the `problem+json` shape, and overwrite `traceId` with the
*current* request's own identifier before returning it. The success-response shape
(`LinkResponse`) has no `traceId` field at all and is unaffected, so it keeps using the original,
simpler `JsonElement` path:

```csharp
private ObjectResult BuildReplayResult(IdempotencyRecord existing)
{
    if (existing.ContentType == "application/problem+json")
    {
        var node = JsonNode.Parse(existing.BodyJson!)!;
        node["traceId"] = HttpContext.TraceIdentifier;
        return new ObjectResult(node) { StatusCode = existing.StatusCode, ContentTypes = { existing.ContentType! } };
    }

    return new ObjectResult(JsonSerializer.Deserialize<JsonElement>(existing.BodyJson!, ReplayJsonOptions))
    {
        StatusCode = existing.StatusCode,
        ContentTypes = { existing.ContentType! },
    };
}
```

Every other field in a replayed response is correctly identical to the first attempt's — that's
the entire point of idempotency. `traceId` is the one deliberate exception: it identifies *this*
HTTP exchange's own trace, not the logical operation's outcome, so it has to be re-derived on every
replay even though nothing else about the response changes.

**Provenance:** found by `feature-review` on commit `05934eb`, fixed in `05f0754`.

## Relatives

### Nodes

- [LinkApi](node:link-api) — the one place `Idempotency-Key` replay exists

### Patterns

- [Caching](pattern:caching) — same project, same `Redis`-backed store shape (two-tier TTL) this
  feature's `IIdempotencyStore` was modeled on
