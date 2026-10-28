# A React unmount guard has to re-arm itself every run, and an in-flight request needs to know if it's still the one being shown

**Category:** concurrency **Status:** fixed

Two related but distinct async-state mistakes, both found in the same component while building DLQ
sample/replay tooling (a non-destructive peek and a destructive replay/remove action on RabbitMQ's
dead-letter queues — see [Async messaging](pattern:async-messaging)'s dead-letter handling), both
worth watching for in any component that fires an async request from an event handler and later
calls `setState` with the result.

**1. A `mountedRef`-style guard that's only ever set `true` once, at `useRef`'s initializer, and
never re-armed inside the effect body.** React 18 `StrictMode` double-invokes effects once in
development — mount, run cleanup, mount again — without ever really unmounting the component. A
guard ref whose cleanup sets it `false` but whose *mount* path never sets it back to `true` latches
to `false` permanently after that first simulated cycle, even though the component is still alive
and will run for the rest of its life. Every later async handler's `setState`, gated behind
`if (mountedRef.current) ...`, silently becomes a no-op from that point on — not a crash, not a
warning, just a UI that quietly stops updating itself, in development only, which makes it
especially easy to miss if dev testing didn't happen to catch it.

**2. An in-flight async request has no way to tell whether it's still "the one currently being
shown"** unless something tracks that explicitly. Disabling a button while its own request is
in-flight (as this panel's Replay button already did) only prevents *that* button from firing
twice — it says nothing about a *different* button's request landing late and overwriting state
that's now showing something else entirely. Reading a piece of component state like
`expandedQueue` from inside the async closure doesn't help either — it's the value from when the
closure was created, not a live read of what's currently selected.

**Concrete instances in this project:** `DlqStatsPanel.tsx`'s pre-existing `mountedRef` (used by
every async handler, including the long-standing `clearDlq`) was only ever set `true` at
`useRef(true)`'s initializer; its cleanup set it `false` on unmount, but nothing set it back to
`true` on a later mount. The result: `clearDlq` likely never visibly updated its own result/error
state in a `StrictMode` dev build even before this feature — only live-verified once Sample/
Replay's own `setState` calls stopped landing too, leaving the panel stuck on "Sampling...".
Separately, the new Sample button's `toggleSample(queueName)` set `expandedQueue`/`sampling`/
`sampleResult` before awaiting the response, then applied that response unconditionally — unlike
Replay, which disables itself globally while in flight, Sample buttons on *other* rows stayed
clickable. A slow response for queue A landing after the operator had already clicked Sample on
queue B would render A's message contents under B's now-expanded row, mislabeling one queue's
dead-lettered payloads as another's.

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

The `mountedRef` re-arm fix is more general than this one panel — it silently fixed the
pre-existing `clearDlq` handler's own result/error state too, which most likely never visibly
updated in a `StrictMode` dev build before this commit landed.

**Provenance:** the `mountedRef` re-arm was found and fixed pre-commit, during manual live-testing
ahead of commit `79d520e` (no separate buggy commit exists to diff for that part — reconstructed as
noted above). The `toggleSample` stale-response race was found by `feature-review` on `79d520e`
(`.notes/review-reports/2026-10-28-1717-dlq-replay-tooling.md`, F2), fixed in `253bd94`.

## Relatives

### Nodes

- [RabbitMQ](node:rabbitmq)

### Patterns

- [Async messaging](pattern:async-messaging)
