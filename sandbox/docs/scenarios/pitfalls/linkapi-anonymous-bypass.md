# Anonymous request bypassed per-user link filtering

**Category:** logic-bug **Status:** fixed

`GET /links` (the "my links" listing) derived `userId` from the JWT's `sub` claim only when a
Bearer token was present, and only added a `Where(x => x.UserId == userId)` filter in that case.
With no token (or an invalid one), the query ran unfiltered — returning every user's links,
including other users' destination URLs and creation timestamps. Since sending the Authorization
header is entirely the caller's choice, any authenticated user could see every other user's links
simply by omitting their own token: a real information disclosure needing no attacker-side auth.

🐛 **Bug** — `LinksController.GetLinks`:

```csharp
var query = _context.Links.AsNoTracking().AsQueryable();
if (User.Identity?.IsAuthenticated == true)
{
    var userId = Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
    query = query.Where(x => x.UserId == userId);
}
// falls through unfiltered when no token is sent
```

✅ **Fix** — `[Authorize]` added to the endpoint; the anonymous branch is gone entirely, so a
valid Bearer token (or the internal API key scheme control-api uses) is always required and the
filter always applies:

```csharp
[Authorize(AuthenticationSchemes = $"{JwtBearerDefaults.AuthenticationScheme},{Constants.InternalApiKeyAuthenticationScheme}")]
[HttpGet]
public async Task<ActionResult<LinksPageResponse>> GetLinks(...)
{
    var query = _context.Links.AsNoTracking();
    if (!User.HasClaim(Constants.InternalClaim, "true"))
    {
        var userId = Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
        query = query.Where(x => x.UserId == userId);
    }
    // ...
}
```

(see review-reports/2026-09-19-2123-0fcdc52-links-pagination.md, F1 — fixed in `b25f4bf`)

## Relatives

### Nodes

- [LinkApi](node:link-api)

### Patterns

None yet.
