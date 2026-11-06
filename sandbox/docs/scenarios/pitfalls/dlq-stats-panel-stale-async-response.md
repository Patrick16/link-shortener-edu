# A React unmount guard has to re-arm itself every run, and an in-flight request needs to know if it's still the one being shown

**Category:** concurrency **Status:** fixed

Two related but distinct async-state mistakes, both found in the same component while building DLQ
sample/replay tooling (a non-destructive peek and a destructive replay/remove action on RabbitMQ's
dead-letter queues — see [Async messaging](pattern:async-messaging)'s dead-letter handling), both
worth watching for in any component that fires an async request from an event handler and later
calls `setState` with the result.

**1. A `mountedRef` guard set `true` only once, at the `useRef` initializer, and never re-armed in
the effect body.** React 18 `StrictMode` double-invokes effects in development (mount → cleanup →
mount again) without truly unmounting. If cleanup sets the ref `false` but the mount path never
resets it `true`, the ref latches `false` permanently — every later async handler's `setState`,
gated behind `if (mountedRef.current) ...`, silently becomes a no-op. No crash, no warning: the UI
just quietly stops updating itself, in dev only, easy to miss.

**2. An in-flight async request can't tell whether it's still "the one currently being shown"**
unless something tracks that explicitly. Disabling a button while its own request is in flight (as
Replay already did) only stops that button from firing twice — it doesn't stop a *different*
button's request landing late and overwriting state that now shows something else. Reading
component state like `expandedQueue` from the async closure doesn't help either: it's the value
captured when the closure was created, not a live read of what's currently selected.

**Concrete instances in this project**, both in `DlqStatsPanel.tsx`:

- **The `mountedRef` gap** predates this feature: used by every async handler including the
  long-standing `clearDlq`, it was only ever set `true` at `useRef(true)`'s initializer, with
  cleanup setting it `false` on unmount and nothing re-arming it. `clearDlq` likely never visibly
  updated its own result/error state in a `StrictMode` dev build — only caught once Sample/Replay's
  own `setState` calls stopped landing too, leaving the panel stuck on "Sampling...".
- **The `toggleSample` race:** `toggleSample(queueName)` set `expandedQueue`/`sampling`/
  `sampleResult` before awaiting the response, then applied it unconditionally. Unlike Replay
  (disabled globally while in flight), Sample buttons on *other* rows stayed clickable — a slow
  response for queue A landing after the operator clicked Sample on queue B would render A's
  payload under B's now-expanded row, mislabeling one queue's dead letters as another's.

⚠️ **Mistake** — `sandbox/frontend/architecture-map/src/components/DlqStatsPanel.tsx` (before
commit `253bd94` for the `toggleSample` race; the `mountedRef` re-arm gap predates this feature
entirely and was never a separate commit to diff — reconstructed from the description in
`.notes/feature-docs/2026-10-28-1729-dlq-replay-tooling.md`, "Bugs found and fixed along the way"
#2, confirmed against the file at `79d520e`, which already carries that part of the fix):

```tsx
// mountedRef: only ever true at its own initializer - reconstructed shape of the pre-existing bug
const mountedRef = useRef(true)
useEffect(() => {
  // nothing re-arms mountedRef.current here
  return () => {
    mountedRef.current = false
  }
}, [])
```

```tsx
// toggleSample (as of 79d520e): no way to tell a stale response from a current one
async function toggleSample(queueName: string) {
  if (expandedQueue === queueName) {
    setExpandedQueue(null)
    return
  }
  setExpandedQueue(queueName)
  setSampling(true)
  setSampleError(null)
  setSampleResult(null)
  try {
    const res = await controlApi.sampleDlq(queueName, SAMPLE_SIZE)
    if (mountedRef.current) setSampleResult(res)
  } catch (err) {
    if (mountedRef.current) setSampleError(err instanceof ControlApiError ? err.message : String(err))
  } finally {
    if (mountedRef.current) setSampling(false)
  }
}
```

✅ **Do this instead** — re-arm the mount guard at the top of the effect body (not just at the
`useRef` initializer), and track which queue's request is actually the one currently being viewed
in a ref the async handler re-checks before applying its response:

```tsx
// Guards async setState calls the same way the poll loop's own `cancelled` flag below guards
// its own - a ref (not state) since unmount detection doesn't need to trigger a re-render, and a
// plain boolean captured by a closure wouldn't survive across separate async handlers. The
// effect body re-arms the ref to true on every (re)run, not just at useRef's initializer - React
// 18 StrictMode double-invokes this effect once in dev (mount -> cleanup -> mount again) without
// actually discarding the component, so without this line the cleanup's `false` from that first
// simulated unmount would stick forever and silently no-op every real async handler's setState
// for the rest of the component's life.
const mountedRef = useRef(true)
useEffect(() => {
  mountedRef.current = true
  return () => {
    mountedRef.current = false
  }
}, [])

// Unlike Replay (disabled globally while one is in flight), Sample buttons on OTHER rows stay
// clickable while a sample request is pending for one row - so a slow response for queue A can
// land after the operator has already clicked Sample on queue B. This ref (not just reading
// `expandedQueue` from the closure, which would be stale) lets the async handler check whether
// ITS OWN request is still the one the user is looking at before applying the response.
const activeSampleQueueRef = useRef<string | null>(null)

async function toggleSample(queueName: string) {
  if (expandedQueue === queueName) {
    activeSampleQueueRef.current = null
    setExpandedQueue(null)
    return
  }
  activeSampleQueueRef.current = queueName
  setExpandedQueue(queueName)
  setSampling(true)
  setSampleError(null)
  setSampleResult(null)
  try {
    const res = await controlApi.sampleDlq(queueName, SAMPLE_SIZE)
    if (mountedRef.current && activeSampleQueueRef.current === queueName) setSampleResult(res)
  } catch (err) {
    if (mountedRef.current && activeSampleQueueRef.current === queueName) {
      setSampleError(err instanceof ControlApiError ? err.message : String(err))
    }
  } finally {
    if (mountedRef.current && activeSampleQueueRef.current === queueName) setSampling(false)
  }
}
```

**Provenance:** the `mountedRef` re-arm was found and fixed pre-commit, during manual live-testing
ahead of commit `79d520e` (no separate buggy commit exists to diff for that part — reconstructed as
noted above). The `toggleSample` stale-response race was found by `feature-review` on `79d520e`
(`.notes/review-reports/2026-10-28-1717-dlq-replay-tooling.md`, F2), fixed in `253bd94`.

## Relatives

### Nodes

- [RabbitMQ](node:rabbitmq)

### Patterns

- [Async messaging](pattern:async-messaging)
