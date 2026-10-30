# A 401-triggered token refresh needs to be deduped across concurrent callers, or it will trip the very reuse-detection it's supposed to avoid

**Category:** resilience-gap **Status:** fixed

Watch for this any time a client holds a short-lived access token backed by a rotating refresh
token with reuse detection (each refresh call invalidates the previous refresh token; replaying an
already-used one revokes the whole session as a suspected theft). The naive fix for "a request got
a 401 because the access token expired" is: catch the 401, call refresh, retry the request. That
looks correct for *one* request. It breaks the moment **more than one** request is in flight when
the token expires — which, for any real UI making several calls close together (a dashboard
loading several panels at once, say), is the common case, not the edge case. Each request
independently catches its own 401 and independently calls refresh; the rotation's own reuse
detection sees the second (and third, and...) refresh call as a replay of an already-consumed
token and revokes the session — the exact failure the retry logic was trying to prevent, just
moved one step later and now affecting every concurrent caller, not only the one that happened to
race.

A second version of the same race exists even with a single always-refreshing caller: if a
*proactive*, timer-based refresh (scheduled ahead of the token's own `exp`) fires at the same moment
a *reactive*, 401-triggered refresh is also in flight, the two independently call refresh and the
second one is again treated as a replay.

**Concrete instance in this project:** `src/frontend/app/src/api/client.ts`'s `apiFetch` had no
401-retry at all before this fix — a 401 simply surfaced as an error to the caller, with no attempt
to refresh and retry. Adding the retry without also deduping concurrent callers would have
reproduced exactly the race described above, since this app already had — and kept — a second,
independent refresh trigger: `AuthContext.tsx`'s pre-existing proactive, `exp`-based refresh timer.

⚠️ **Mistake** — the naive version of this fix, not what shipped (there is no commit showing this
broken shape in this project's history — it's shown to make the failure mode concrete, since the
dedup was built in from the start rather than added after a race was observed in production):

```ts
// NAIVE - works for exactly one caller, breaks the instant a second one races it
if (!response.ok && response.status === 401 && auth) {
  await authApi.refresh() // each concurrent 401 independently calls this
  return apiFetchInternal(baseUrl, path, options, false) // retry
}
```

```ts
// AuthContext.tsx's proactive timer, calling refresh independently of the retry path above -
// the two can fire at the same moment and race each other into two concurrent refresh calls.
scheduleRefresh(() => authApi.refresh())
```

✅ **Do this instead** (`5637b49`) — one shared, module-level in-flight promise that both the
reactive (401) and proactive (timer) paths call through, so a second caller joins the first
in-flight refresh instead of starting its own:

```ts
// client.ts
let refreshPromise: Promise<string> | null = null

// Exported so AuthContext's own proactive timer can share this exact in-flight promise too, not
// just apiFetch's reactive 401 path below - otherwise the two could still independently race
// each other into two concurrent POST /refresh calls right as a token's exp passes under load,
// the same replay-detection risk this dedup exists to prevent.
export function requestTokenRefresh(): Promise<string> {
  if (!refreshHandler) return Promise.reject(new Error('No refresh handler registered'))
  refreshPromise ??= refreshHandler().finally(() => { refreshPromise = null })
  return refreshPromise
}

async function apiFetchInternal<TResponse>(/* ... */, retryOn401: boolean): Promise<TResponse> {
  // ...
  if (!response.ok) {
    // Only for calls that actually attached a Bearer token - login/register/refresh themselves
    // never pass auth: true, so a 401 from refresh itself correctly falls through to the throw
    // below instead of looping back into itself.
    if (response.status === 401 && auth && retryOn401) {
      try {
        await requestTokenRefresh()
        return apiFetchInternal(baseUrl, path, { /* ...same options... */ }, false)
      } catch {
        // Refresh itself failed - fall through and report the original request's own 401.
      }
    }
    throw new ApiError(response.status, await extractErrorMessage(response))
  }
}
```

`AuthContext.tsx`'s existing proactive timer was then rewired to call `requestTokenRefresh()`
instead of `authApi.refresh()` directly, so both triggers share the exact same in-flight promise.
Four new test cases cover: success retry, no-retry-without-`auth: true`, refresh-failure falling
through to the original 401, and the dedup case itself (two concurrent 401s resulting in exactly
one `POST /refresh`).

**The reusable lesson:** a 401-retry-with-refresh implementation is incomplete until it's been
checked against *every* place in the client that can independently trigger a refresh — not just
the new retry path being added. Pick one shared in-flight promise (or equivalent single-flight
mechanism) as the one and only place a refresh call is actually issued from, and route every
trigger through it.

(not sourced from a `.notes/review-reports/` finding — found and fixed same-day as part of the
external code review batch; see `.notes/feature-docs/2026-10-30-1524-external-review-10-bugs.md`,
bug 10, and `.notes/PLAN.md`'s "Feature: External Review Findings" section — fixed in `5637b49`)

## Relatives

### Nodes

- [AuthApi](node:auth-api) — owns the refresh-token rotation and reuse-detection this dedup protects
  against tripping; see its own
  [concurrent refresh-token rotation race](pitfall:auth-refresh-token-rotation-race) pitfall for the
  server-side half of this same mechanism

### Patterns

None yet.
