# A control-plane process's in-memory cache of "what's currently toggled" has no source of truth once it restarts

**Category:** architecture-bug **Status:** fixed

Watch for this any time a long-running process caches the state of something *external* it doesn't
exclusively own, purely in memory, and later uses that cache to decide how to mutate the external
thing again. The cache is correct right up until the process restarts — at that point it silently
resets to whatever default the code was written with, while the external thing it was describing
hasn't changed at all. Nothing fails loudly: the process comes back up fine, answers every read
with its (now wrong) cached values, and the bug only surfaces on the *next* write that trusts the
cache instead of the real world.

**Concrete instance in this project:** `InfraToggleService` mixes two kinds of state — a few fields
read live from Docker on every call (`HaproxyEnabled`, by design, per its own comment and the
`haproxy-toggle-cached-state-bypassed-by-generic-controls` pitfall below), and roughly a dozen
private fields (`_pgcatEnabled`, `_cacheEnabled`, `_messagingMode`, `_redisConnectionViaSentinel`,
`_mongoConnectionIncludesReplicas`, `_npgsqlPoolSize`, `_circuitBreakersEnabled`,
`_geoIpBulkheadLimit`, `_rabbitMqPrefetch`, `_mongoHedgeDelayMs`, `_l1CacheEnabled`,
`_negativeCacheEnabled`) that are plain in-memory defaults, reset on every control-api process
restart. `CurrentStandingEnv()` — used by *every* "recreate this container" call the control plane
makes, for *any* toggle, not just the one that just changed — builds its env vars from those cached
fields. So after a control-api restart, the next unrelated toggle flip (say, just switching
messaging mode) silently recreates every other service using the stale hardcoded defaults for every
*other* field too — potentially pointing a service at infrastructure that isn't actually running
(e.g. recreating with `ConnectionStrings__Redis` back in Sentinel form while the Sentinel containers
are stopped, because `_redisConnectionViaSentinel` reset to its default and nothing told it
otherwise).

The two fix directions worth knowing about, and why one was rejected: persist the state to a file
(mirroring this project's own `ScenarioStore`/`PresetStore`), or reconcile the in-memory fields from
the live containers on startup. The file approach was rejected specifically because it would be a
*second* independently-stale copy of exactly the same information — `ScenarioStore`/`PresetStore`
persist genuinely user-authored content with no other source of truth, but this toggle state's real
source of truth is always the already-running containers themselves, which a live read can reach
directly. Reconciling from the containers was chosen instead.

⚠️ **Mistake** — `sandbox/infra/control-api/Services/InfraToggleService.cs` (before `5637b49`, same
shape as `haproxy-toggle-cached-state-bypassed-by-generic-controls`'s "as shipped in `4c789d3`"
snippet, just with more fields added over time):

```csharp
// In-memory standing toggle state - resets to these hardcoded defaults on every control-api
// restart, with nothing re-deriving them from whatever is actually running.
private bool _pgcatEnabled = true;
private bool _cacheEnabled = true;
private string _messagingMode = "rabbitmq";
private bool _redisConnectionViaSentinel;
// ... _mongoConnectionIncludesReplicas, _npgsqlPoolSize, _circuitBreakersEnabled,
// _geoIpBulkheadLimit, _rabbitMqPrefetch, _mongoHedgeDelayMs, _l1CacheEnabled,
// _negativeCacheEnabled - same shape, same problem.

// CurrentStandingEnv() builds env vars for the NEXT recreate from these cached fields -
// whichever toggle call happens to run next recreates every service using every field's
// current (possibly stale-since-restart) value, not just the one field that changed.
```

✅ **Do this instead** (`5637b49`) — reconcile every cached field from the real, already-running
containers once, at startup, before any toggle can act on stale defaults:

```csharp
// Called once at control-api startup by ToggleStateReconcilerService (a BackgroundService, so a
// slow/unreachable Docker socket at boot doesn't block control-api's own startup). Re-derives
// each cached field from the real env vars the already-running containers were last recreated
// with, so the very next toggle flip after a restart carries the ACTUAL current config forward.
public async Task ReconcileFromLiveContainersAsync(CancellationToken ct)
{
    var linkApiEnv = await GetContainerEnvAsync("link-api", ct);
    if (linkApiEnv is not null)
    {
        if (linkApiEnv.TryGetValue("ConnectionStrings__Postgres", out var postgresConnStr))
            _pgcatEnabled = postgresConnStr.Contains("Host=haproxy", StringComparison.OrdinalIgnoreCase);
        if (linkApiEnv.TryGetValue("Cache__Enabled", out var cacheEnabled))
            _cacheEnabled = cacheEnabled == "true";
        if (linkApiEnv.TryGetValue("ConnectionStrings__Redis", out var redisConn))
            _redisConnectionViaSentinel = redisConn.Contains("sentinel", StringComparison.OrdinalIgnoreCase);
        // ... messaging mode, circuit breakers, pool size - same "read the real rendered env var,
        // only overwrite the cached field if the container actually exists" shape.
    }
    else
    {
        // Best-effort, not an error: a container not existing yet (control-api started before
        // `docker compose up` ever ran) just means this field keeps its hardcoded default.
        logger.LogInformation("link-api not running yet - keeping hardcoded toggle defaults for its fields");
    }
    // ... traffic-service, reporting-api cover the rest of CurrentStandingEnv()'s fields.
}
```

A container missing at startup is treated as a normal, non-error case — logged at Information
level, not a warning — because "control-api came up before the stack did" is an expected sequence,
not a broken one; the field simply keeps its hardcoded default until a real toggle flip sets it for
real.

**See also** this same fix's own, narrower sub-pitfall:
[the first reconciliation attempt read docker-compose's `${VAR}` substitution names instead of the
real rendered env vars](pitfall:control-api-reads-compose-substitution-names-not-rendered-env) — a
separate, reusable mistake about *how* to read a container's env correctly, distinct from *whether*
to reconcile at all (this doc).

(not sourced from a `.notes/review-reports/` finding — found and fixed same-day, pre-commit, as part
of the external code review batch; see `.notes/PLAN.md`'s "Feature: External Review Findings"
section and `.notes/feature-docs/2026-10-30-1524-external-review-10-bugs.md`, bug 2, for the fuller
narrative — fixed in `5637b49`)

## Relatives

### Nodes

- [PgCat](node:pgcat) — the toggle whose restart-desync was actually live-verified
  (`docker inspect` confirmed `pgcat=True` was stuck even with pgcat really off, until this fix)
- [Redis Sentinel](node:redis-sentinel-1) — the Sentinel-form-vs-direct field cited as the concrete
  "points at infra that isn't up" failure scenario

### Patterns

- [Pooler scaling](pattern:pooler-scaling)
- [High availability & failover](pattern:high-availability-failover)
