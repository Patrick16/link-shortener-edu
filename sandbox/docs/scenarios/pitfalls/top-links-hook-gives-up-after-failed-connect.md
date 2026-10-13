# A real-time connection hook gave up permanently after one bad start

**Category:** resilience-gap **Status:** fixed

A client-side library's own "automatic reconnect" only covers reconnecting an *already-established*
connection. It does not cover the initial `start()` failing, and it does not retry forever — once
it exhausts its own attempt budget, it calls back and stops trying. Code that only handles the
"connected, then dropped" case and treats everything else as a terminal "offline" state leaves the
UI stuck until a full page reload, even once the server it's trying to reach is healthy again.

**Concrete instance in this project:** `useTopLinks` (the hook behind the dashboard's live
top-links panel) called `connection.start()` once and set `'offline'` on failure, with no retry.
Separately, `HubConnectionBuilder().withAutomaticReconnect()` uses a default backoff schedule that
gives up after 4 attempts (about 42 seconds) and fires `onclose` — which this hook also mapped
straight to `'offline'`, again with nothing to recover. Either path (ReportingApi not yet up when
the dashboard loads, or a restart that outlasts the reconnect window) left the panel showing
"Offline" indefinitely even after the backend came back, since nothing was watching for that.

A second, narrower bug came along with the same code: cleanup's `connection.stop()` sets
`setStatus('connecting')` as a transitional state, but the unguarded `onclose()` handler could fire
*after* that and overwrite it back to `'offline'` — a stale callback from the connection being torn
down, not a real new disconnect.

Mistake (`bd47119`, `src/frontend/app/src/hooks/useTopLinks.ts`):

```typescript
connection.onclose(() => setStatus('offline'))

let stopped = false
connection
  .start()
  .then(() => {
    if (!stopped) setStatus('live')
  })
  .catch(() => {
    if (!stopped) setStatus('offline')
  })
```

Do this instead (`a2b405e`, same file) — a fixed retry delay, reused for both a failed `start()`
and a `withAutomaticReconnect`-exhausted `onclose`, and a `stopped` guard that now also cancels a
pending retry:

```typescript
const RETRY_DELAY_MS = 10_000

let stopped = false
let retryTimer: ReturnType<typeof setTimeout> | undefined

function scheduleRetry() {
  if (stopped) return
  setStatus('offline')
  retryTimer = setTimeout(start, RETRY_DELAY_MS)
}

function start() {
  connection
    .start()
    .then(() => {
      if (!stopped) setStatus('live')
    })
    .catch(scheduleRetry)
}

// Fires once the client's own automatic reconnect has given up. Restarting the same connection
// is allowed once it's disconnected, so the retry reuses it rather than building a new one.
connection.onclose(scheduleRetry)
start()
```

The `stopped` flag (already present before this fix, for a different reason — React StrictMode's
mount/unmount/remount can leave a `start()` still pending when `stop()` runs) now also has to clear
`retryTimer` in the cleanup function, or a scheduled retry could fire after the component using
this hook has already unmounted.

**Provenance:** before and after are real commits (`bd47119` and `a2b405e`). The fix added a unit
test (`useTopLinks.test.tsx`) that mocks a failed first `start()` followed by a successful retry
and asserts the hook recovers to `'live'` without a remount.

## Relatives

### Nodes

- [ReportingApi](node:reporting-api) — the server this hook connects to

### Patterns

None yet.
