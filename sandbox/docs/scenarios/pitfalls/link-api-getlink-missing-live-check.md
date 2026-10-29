# A soft-delete/expiry check duplicated across two read endpoints only got written into one of them

**Category:** architecture-bug
**Status:** fixed

When "is this record still live" isn't a column you can filter in one place but a check repeated
by hand at every endpoint that reads the record, each new read path has to remember to re-derive
it independently — and nothing enforces that it does. Watch for this whenever a cache-aside entity
carries its own soft-delete/expiry fields (`DeletedAt`, `ExpiresAt`) instead of a query the
database itself can filter: a listing endpoint that already excludes gone records and a
single-item endpoint that fetches the exact same entity are an obvious pair to keep in sync, but
nothing short of actually reading both call sites side by side catches a forgotten check in one of
them.

**Concrete instance in this project:** `LinkApi`'s `GetLinks` (the "my links" listing) already
filtered out deleted and expired links. `GetLink` (`GET /Links/{hash}`, a single-hash lookup
through the same cache-aside path `RedirectApi` uses) fetched the identical `Link` entity but never
checked `DeletedAt`/`ExpiresAt` at all — it just returned whatever `IEntityCacheService<Link>.
GetOrFetch` handed back, cached or not. A caller hitting `GetLinks` would correctly see a deleted
hash disappear from the list, then `GET /Links/{hash}` on that exact same hash would still return
its metadata as if the link were live — two endpoints in the same service, reading the same
record, giving contradictory answers about whether it still exists.

⚠️ **Mistake** — `src/backend/Services/LinkApi/Controllers/LinksController.cs` (`GetLink`, before
`383d2d9`):
```csharp
var res = await _entityCacheService.GetOrFetch(hash, hash, cancellationToken);
if (res is null)
{
    return NotFound();
}

return new LinkResponse(res.ShortenLink, res.CreatedAt);
```

✅ **Do this instead** — mirror the exact same live check `RedirectApi`'s own redirect handler
already applies right after its own `GetOrFetch` call, so both read paths agree:
```csharp
var res = await _entityCacheService.GetOrFetch(hash, hash, cancellationToken);
if (res is null)
{
    return NotFound();
}

// Mirrors RedirectApi's own live check (RedirectController.cs) - a deleted/expired link's
// row (or its optimistic/cached copy) can still be present until the deletion consumer or
// the TTL sweep catches up, but it must not be readable as if it were still active.
if (res.DeletedAt is not null || (res.ExpiresAt is { } expiresAt && expiresAt <= DateTime.UtcNow))
{
    return Problem(
        detail: "This link has expired or been deleted.",
        statusCode: StatusCodes.Status410Gone,
        title: "Link gone.");
}

return new LinkResponse(res.ShortenLink, res.CreatedAt);
```

The fix is a deliberate duplication, not a shared helper — the same inline check now lives in both
`LinksController.GetLink` and `RedirectController`'s redirect handler, returning a JSON
`ProblemDetails` body in one and rendering an HTML "gone" page in the other. If a third call site
ever needs the same check, that's the point to factor it out, not before.

(see `.notes/review-reports/2026-10-16-1208-security-audit-followup-fixes.md`; found by an
external security audit, not `feature-review` — fixed in `383d2d9`)

## Relatives

### Nodes

- [LinkApi](node:link-api) — both the bug and the fix live here
- [RedirectApi](node:redirect-api) — the sibling read path whose existing live check this one now mirrors

### Patterns

- [Caching](pattern:caching) — the cache-aside entity both endpoints read through
