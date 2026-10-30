# A backend idempotency guard only protects a retry if the client actually sends the key it's keyed on

**Category:** resilience-gap **Status:** fixed

Watch for this any time a backend endpoint adds idempotency-key support — it closes the "client
timeout + automatic/manual retry creates a duplicate" hole only for callers that actually send the
header. The server-side half (claim/complete/replay, TTL, the 256-char cap, whatever else) can be
fully built and fully tested and still protect nothing in practice, because the feature is opt-in
from the caller's side by construction — nothing forces a client to send it, and nothing on the
server can tell "a genuinely new request" apart from "a retry that just forgot the key" once it
arrives without one.

**Concrete instance in this project:** `LinkApi`'s `LinksController.CreateLink` has supported an
`Idempotency-Key` header since the Resilience & Scale-Out Backlog's item 4 — see
[LinkApi](node:link-api)'s own `TryClaimAsync`/`CompleteAsync`/`EvictAsync` flow and its two
existing idempotency pitfall-docs. The product frontend (`src/frontend/app`), the one real caller
of this endpoint in this project, never sent the header at all. A client-side timeout followed by a
retry (automatic, from a flaky network, or just the user clicking "Create" again not realizing the
first click went through) could still create two links for one logical action — exactly the
scenario the backend feature exists to prevent, left wide open on the one path that could actually
trigger it.

⚠️ **Mistake** — `src/frontend/app/src/api/linkApi.ts` (before `5637b49`):

```ts
export function createLink(originalLink: string, alias?: string, ttlSeconds?: number): Promise<LinkResponse> {
  const request: LinkCreateRequest = { originalLink, ...(alias ? { alias } : {}), ...(ttlSeconds ? { ttlSeconds } : {}) }
  return apiFetch<LinkResponse>(LINK_API_URL, '/links', { method: 'POST', body: request, auth: true })
}
```

✅ **Do this instead** (`5637b49`) — attach a fresh key per logical call, not per network attempt,
so a genuine retry of the same action reuses it (correctly treated as a replay) while a real second
action (a different link) always gets a new one:

```ts
// Idempotency-Key: a fresh uuid per call means a genuine second call (the user clicking "Create"
// again for a different link) is never mistaken for a replay - only an actual retry of the exact
// same request object reuses this same key, since crypto.randomUUID() is called once per
// createLink() invocation, not per fetch attempt (so apiFetch's own internal 401-retry, see the
// companion pitfall on that, correctly replays with the same key instead of minting a new one).
export function createLink(originalLink: string, alias?: string, ttlSeconds?: number): Promise<LinkResponse> {
  const request: LinkCreateRequest = { originalLink, ...(alias ? { alias } : {}), ...(ttlSeconds ? { ttlSeconds } : {}) }
  return apiFetch<LinkResponse>(LINK_API_URL, '/links', {
    method: 'POST',
    body: request,
    auth: true,
    headers: { 'Idempotency-Key': crypto.randomUUID() },
  })
}
```

Generating the key **once per logical invocation, not once per fetch attempt**, is the detail that
makes this correct rather than merely present: if a fresh key were minted on every underlying
network attempt (including the client's own internal 401-retry), the backend would see each retry
as a brand-new request and the whole point of the header would be defeated.

**The reusable lesson:** when wiring up a client against a backend idempotency feature, audit every
real caller for whether it actually sends the key — a fully-correct server-side implementation with
zero client adoption is functionally equivalent to not having built it, for every caller that
doesn't send it.

(not sourced from a `.notes/review-reports/` finding — found and fixed same-day as part of the
external code review batch; see `.notes/feature-docs/2026-10-30-1524-external-review-10-bugs.md`,
bug 10, and `.notes/PLAN.md`'s "Feature: External Review Findings" section — fixed in `5637b49`,
live-verified by creating a link through the running UI and confirming
`idempotency:links-create:<uuid>` landed in Redis)

## Relatives

### Nodes

- [LinkApi](node:link-api) — owns the server-side idempotency claim this key actually protects

### Patterns

None yet.
