# A slot-release function that mutates shared fields directly, not a value scoped to its own caller, can tear down someone else's turn

**Category:** race-condition **Status:** fixed

Watch for this any time a "release my turn" cleanup step is called from more than one place for the
same unit of work (a success path *and* a safety-net `finally`, say), and that cleanup mutates
shared fields directly rather than checking it still owns what it's about to tear down. The two call
sites look redundant and harmless individually — "release twice, the second one's a no-op" is a
reasonable mental model right up until the *first* release lets a brand-new unit of work claim the
same slot before the *second*, delayed call finally runs. At that point "release" doesn't mean "my
turn is over," it means "whatever is currently in this field is over" — and the second caller
clobbers a stranger's state.

**Concrete instance in this project:** `TrafficRunCoordinator` is control-api's "only one k6 load
test runs at a time" gate — `RunResourceMaxTracker`, the live resource-peak tracker it drives, isn't
safe for two concurrent runs. The original design was a `Lock _gate` guarding a plain `bool
_runActive` and a nullable `CancellationTokenSource? _runCts`, released from two call sites: an
early release right after the k6 process itself exits (a latency optimization — the snapshot save
that follows has its own ~2s trace-flush delay that doesn't need the slot held), and a `finally`
block as a safety net for the cancellation/exception paths. Both release call sites called the same
`ReleaseSlot()`, which unconditionally nulled `_runCts` and cleared `_runActive` — not "release the
slot for *this* run," just "release the slot, whatever currently holds it." If a run's own
`finally`-block release ran late enough — after that same run had already done its early release,
and a *second* run had already claimed the slot and installed its own new `_runCts` — the first
run's delayed `finally` call would dispose and null out the *second* run's CTS and bool state
instead of being the no-op it was supposed to be, breaking the "one run at a time" invariant the
whole class exists to guarantee.

⚠️ **Mistake** — `sandbox/infra/control-api/Services/TrafficRunCoordinator.cs` (before `5c8475b`):

```csharp
private readonly Lock _gate = new();
private bool _runActive;
private CancellationTokenSource? _runCts;

private void ReleaseSlot()
{
    lock (_gate)
    {
        // Unconditional - clears whatever is currently in these fields, not specifically the
        // run that's calling this. Called from two places per run (an early release right after
        // k6 exits, and RunAsync's own finally as a safety net) - if the first call's effects
        // (a new run claiming the slot) land before the second, delayed call runs, that second
        // call tears down the NEW run's state instead of being a no-op.
        _runCts?.Dispose();
        _runCts = null;
        _runActive = false;
    }
}
```

✅ **Do this instead** (`5c8475b`) — identify the release by the specific `CancellationTokenSource`
the calling run owns, and only act if the shared field still holds exactly that instance:

```csharp
private readonly SemaphoreSlim _runSlot = new(1, 1);
private CancellationTokenSource? _activeRunCts;

// Releases the slot for exactly one run, identified by its own cts - never whatever cts the
// field currently happens to hold. Called (up to) twice per run: the early release right after
// k6 exits, and the finally-block safety net. CompareExchange guarantees only the FIRST of those
// two calls to actually run for a given cts ever swaps the field and releases the semaphore -
// nothing else can change _activeRunCts away from this cts before then, and by the time
// Release() makes the slot available to a brand-new TryStart, the field has already been
// nulled - so a new run's cts can never be clobbered by this run's second (by then stale) call.
private void ReleaseIfOwner(CancellationTokenSource cts)
{
    if (Interlocked.CompareExchange(ref _activeRunCts, null, cts) == cts)
    {
        cts.Dispose();
        _runSlot.Release();
    }
}
```

The reusable lesson: a "release my turn" function needs to know *whose* turn it's releasing, not
just that *a* turn is currently held — identify ownership by the specific token/handle the caller
was given, and make the release a conditional swap on that identity, not an unconditional clear of
shared state. This is the same shape as two other fixes in the very same commit
(`EntityCacheService`'s single-flight cache removing its in-flight `Lazy` by key *and* value instead
of key alone, and the SQLite fallback queue's claim-release path) — worth recognizing as one
recurring bug family, not three unrelated ones.

**Honestly flagged gap left behind, not yet closed:** this fix was verified by engineering the exact
adversarial timing live against the real Docker stack (a fast 1-iteration run releasing its slot
early while a second, longer run claimed it seconds before the first run's own `finally` cleanup
ran) rather than by a new automated test — `sandbox/infra/tests/ControlApi.Tests` has no
`TrafficRunCoordinator`-specific test file. A future refactor of `TryStart`/`ReleaseIfOwner` could
silently reintroduce the same ownership-transfer race with nothing in CI to catch it. See
`.notes/review-reports/2026-11-04-2129-close-5-bugs-round-2.md`, F2 (still `open` at the time of
writing).

(see `.notes/review-reports/2026-11-04-2129-close-5-bugs-round-2.md` — not a numbered finding in
that report itself (pre-identified by external review and confirmed by inspection, not a separately
numbered finding), fixed in `5c8475b`)

## Relatives

### Nodes

- [k6](node:k6) — the load-test runner whose "one run at a time" invariant this class exists to
  guarantee (no node-doc exists for k6 yet, so this pitfall is currently standalone — not linked
  from any node/pattern doc's own Pitfalls list)
