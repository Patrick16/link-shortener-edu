# Disabling a button while "any" of several independent async operations is pending needs `||`, not `&&`

**Category:** race-condition **Status:** fixed

Watch for this any time a UI control is disabled based on more than one in-flight async operation,
and the two operations are genuinely independent (different endpoints, different latencies, no
guarantee either resolves before the other). A `disabled={aLoading && bLoading}` guard reads as
"disable while busy," but it actually only disables while **both** are still pending — the instant
either one resolves, the control re-enables even though the other is still running. A second click
in that window fires a new, overlapping request for whichever field is still pending. If the two
overlapping requests for that same field resolve out of order — entirely plausible under real
network conditions, e.g. the first getting stuck behind a slow downstream query the second one
skips past — the *older* response's callback can land after the *newer* one already rendered,
silently overwriting fresh data with stale data. Nothing in the UI indicates this happened; the
panel just quietly shows old numbers.

**Concrete instance in this project:** `DashboardPage`'s "Refresh stats" button forces a re-fetch of
both a link's click summary and its recent-clicks list — two independent requests that typically
resolve at different times. The button's `disabled` expression used `&&` where it needed `||`.

⚠️ **Mistake** — `src/frontend/app/src/pages/DashboardPage.tsx` (as shipped in `5637b49`):

```tsx
disabled={summaries[item.shortenLink] === 'loading' && recentClicks[item.shortenLink] === 'loading'}
```

Failure scenario: user clicks "Refresh stats"; `getRecentClicks` resolves quickly while
`getClickSummary` is still pending; the button is clickable again during that window (only one of
the two is `'loading'`, so `&&` is already `false`); a second click fires a second, overlapping
`getClickSummary` call for the same hash; if the two in-flight summary requests resolve out of
order, the slower/older response's `.then()` overwrites the UI with stale data after the newer one
already rendered — the panel ends up showing an older click summary than what's actually current,
with no visual indication anything is wrong.

✅ **Do this instead** (`5cce8f3`) — disable while *either* is still pending, closing the window
where the overlapping second fetch is even possible:

```tsx
// || not && (feature-review F1): summary and recent-clicks are independent fetches that
// typically resolve at different times - with &&, the button re-enabled as soon as either one
// resolved while the other was still in-flight, letting a second click fire an overlapping
// fetch for the same field whose out-of-order response could silently overwrite fresher data.
disabled={summaries[item.shortenLink] === 'loading' || recentClicks[item.shortenLink] === 'loading'}
```

The regression test added alongside this fix is the real proof the lesson generalizes beyond "swap
one operator": it holds *one* of the two fetches open (an unresolved promise) while letting the
other resolve, then asserts the button stays disabled throughout that whole window — a test that
would pass against the broken `&&` version the moment either fetch resolved is exactly the kind of
test that would have caught this the first time.

**This is a one-character diff with a two-sided lesson:** the bug class ("disable while *any* of N
independent operations is pending" needs `||` over all N, not `&&`) is worth checking for anywhere
else in a codebase a button's `disabled` state is computed from more than one independent loading
flag — it reads correctly by accident whenever the two operations happen to resolve close enough
together in practice, which is exactly what makes it easy to ship without noticing locally.

(see `.notes/review-reports/2026-10-30-1520-close-10-review-bugs.md`, F1 — logged there as a plain
`logic-bug` finding, but the actual failure mode is a genuine race between two independently-
resolving async requests, not a simple output mistake, so it's promoted here as `race-condition`,
the same judgment call this series has made before for findings whose category tag undersold their
actual mechanism — fixed in `5cce8f3`)

## Relatives

### Nodes

None — this bug lives entirely in the product frontend (`src/frontend/app`), which has no node doc
in this tree (see `sandbox/docs/scenarios/nodes/` — that directory documents the backend/infra
graph `architecture-map` renders, not the product's own UI).

### Patterns

None yet.
