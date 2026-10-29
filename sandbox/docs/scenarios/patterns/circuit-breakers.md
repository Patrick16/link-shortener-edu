## What it is

A circuit breaker ([Polly](https://www.pollydocs.org/) v8's `ResiliencePipeline` here) wraps a call
to a dependency and tracks its recent failure rate. Once failures cross a threshold within a
sampling window, the breaker "opens": every call fails immediately with `BrokenCircuitException`
instead of attempting the real call, for a fixed break duration. After that, it goes "half-open"
and lets exactly one probe call through — success closes the breaker again, failure re-opens it.

## What it solves

Every dependency call in this project already degrades gracefully when it fails — a Redis miss
falls through to Postgres, a RabbitMQ publish failure spills into the SQLite fallback queue (see
[Caching](pattern:caching) and [Async messaging](pattern:async-messaging)). That's fail-*open*, not
fail-*fast*: a plain `try`/`catch` can only react once the failing call actually fails, which means
every single request still pays that dependency's full connect/timeout cost first. If a dependency
isn't just erroring occasionally but is genuinely dead (container stopped, network partition), that
cost is paid on *every* request, even though the eventual outcome never changes. A circuit breaker
short-circuits that: once it's learned a dependency is down, it rejects calls immediately instead of
letting each one time out on its own, and periodically re-tests on its own schedule so recovery is
detected without flooding the dependency with probes.

This is a pure latency/throughput optimization layered on top of existing fail-open behavior — it
does not change what happens on an isolated, non-systemic failure, and for most of this project's
call sites it doesn't even change the response shape, since the fail-open `try`/`catch` underneath
is unchanged.

## How it works

One shared builder, `BuildCircuitBreaker(dependencyName, logger)`
(`src/backend/Shared/Common/ResiliencePipelines.cs:20`), configures the same tuning for every call
site that uses it:

```csharp
// src/backend/Shared/Common/ResiliencePipelines.cs:20-69 (shape, trimmed)
new ResiliencePipelineBuilder()
    .AddCircuitBreaker(new CircuitBreakerStrategyOptions
    {
        FailureRatio = 0.5,
        MinimumThroughput = 10,
        SamplingDuration = TimeSpan.FromSeconds(30),
        BreakDuration = TimeSpan.FromSeconds(15),
        ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not OperationCanceledException),
        OnOpened = /* log + increment resilience.circuit_breaker.transitions{dependency,state=opened} */,
        OnHalfOpened = /* ...state=half-opened */,
        OnClosed = /* ...state=closed */,
    })
    .Build();
```

- **Needs at least 10 calls in a rolling 30s window with ≥50% failing before it opens** — a handful
  of isolated failures never trips it; only a sustained failure rate does.
- **Stays open for 15s**, then lets one half-open probe through before deciding whether to close
  again.
- **`OperationCanceledException` never counts as a failure** — a client disconnect or an upstream
  deadline firing is not the dependency's fault, and counting it would let an unrelated flood of
  cancellations trip the breaker for every other caller too.
- **Every transition is logged and counted** (`resilience.circuit_breaker.transitions`, an OTel
  counter tagged by `dependency` and `state`). Without this, an open breaker "silently heals" the
  request path — everything just starts failing fast — with nothing anywhere signaling that a
  dependency is currently being bypassed.

```mermaid
stateDiagram-v2
    [*] --> Closed
    Closed --> Open: failure ratio >= 50% over a 30s window (>= 10 calls)
    Open --> HalfOpen: after 15s break duration
    HalfOpen --> Closed: probe call succeeds
    HalfOpen --> Open: probe call fails
    Closed --> Closed: call succeeds, or fails under threshold
```

In `Open`, every call fails immediately with `BrokenCircuitException` — the real dependency is
never touched until the breaker transitions to `HalfOpen` and lets one probe through.

**Keyed DI, one breaker instance per dependency per process.** Each wrapped dependency gets its own
`ResiliencePipeline` registered as a *keyed* singleton, keyed by a constant
(`Constants.RedisCircuitBreakerKey`/`GrpcCircuitBreakerKey`/`RabbitMqCircuitBreakerKey`) rather than
a plain singleton — a single process can host more than one of these (a service that both caches in
Redis and dispatches over gRPC needs two independent breakers, not one type-keyed registration
fighting over which dependency it represents). Registration sits next to each dependency's own
extension method, so the breaker's lifetime matches the dependency's.

## How it's implemented here

Four call sites wrap the shared builder's output around a single dependency call, inside the
`try`/`catch` that already existed:

1. **`EntityCacheService<T>`/`LinkCacheService`** (Redis breaker) — `CacheAsync`/
   `InvalidateCacheAsync`/`GetCachedAsync` each wrap their single Redis call in
   `_resilience.ExecuteAsync(...)`. `BrokenCircuitException` is just another `Exception` to the
   existing catch block, so it falls straight into "treat as a cache miss / continue without
   caching" — see [Caching](pattern:caching).
2. **`CachingGeoIpResolver`** — reuses the *same* Redis breaker instance (there is one Redis breaker
   per process, not one per consumer of Redis).
3. **`SyncGrpcDispatcher`** (gRPC breaker) — the messaging-mode toggle's synchronous transport
   (`Messaging:Mode=grpc`). This is the one call site with **no** fail-open fallback by design: a
   `BrokenCircuitException` (or the underlying `RpcException`) propagates out to the controller and
   from there to `GlobalExceptionHandler`, which maps it to a `503 Service Unavailable`
   `ProblemDetails` response. It's the only one of the four breakers whose state is ever visible
   outside logs/metrics, for the same reason the sync-gRPC mode exists at all — see
   [Async messaging](pattern:async-messaging).
4. **`RabbitMqPublisher`** (RabbitMQ breaker) — wraps only `RentChannelAsync` (opening/reusing a
   pooled AMQP channel), deliberately **not** the subsequent `BasicPublishAsync` call, so the
   existing channel-release bookkeeping in `finally` keeps working unchanged regardless of which
   call failed. A rejected rent (via the breaker or otherwise) falls through to the pre-existing
   SQLite fallback save — the breaker makes reaching that fallback cheaper, it doesn't replace it.

**A fifth dependency, ip-api.com's `HttpClient`, uses a different mechanism entirely**: `HttpClient`
already has its own idiomatic resilience hook
(`Microsoft.Extensions.Http.Resilience`'s `AddResilienceHandler`), so `GeoIpExtensions` attaches a
breaker directly to the typed client instead of going through `ResiliencePipelines.BuildCircuitBreaker`
— same tuning values, hand-copied rather than shared, plus an extra rule treating any HTTP 5xx
response as a failure.

```csharp
// src/backend/Shared/Common/Constants.cs:47-52
public const string RedisCircuitBreakerKey = "redis";
public const string GrpcCircuitBreakerKey = "grpc";
public const string RabbitMqCircuitBreakerKey = "rabbitmq";
```

## A related pattern: bulkhead isolation

A circuit breaker only reacts to a **failure ratio** — it does nothing if a dependency is merely
*slow* rather than down. TrafficService and ReportingService's ip-api.com geo-IP lookups (the fifth
dependency above, point 5) sit behind a second, independent Polly v8 strategy that covers exactly
that gap: a concurrency limiter (`AddRateLimiter`) capping how many `ip-api.com` calls either
service can have in flight at once, rejecting anything over the limit immediately instead of
letting it queue. It's named "bulkhead" after the shipbuilding term — sealing off one slow
compartment so it can't flood and starve everything else sharing the same process (HttpClient
connections, thread-pool work).

```csharp
// src/backend/Shared/Infrastructure/GeoIpExtensions.cs:49-93 (shape, trimmed)
.AddResilienceHandler("geo-ip-circuit-breaker", (resilience, context) =>
{
    resilience
        .AddRateLimiter(new RateLimiterStrategyOptions
        {
            DefaultRateLimiterOptions = new ConcurrencyLimiterOptions
            {
                PermitLimit = maxConcurrentRequests,   // GeoIp:MaxConcurrentRequests, default 10
                QueueLimit = 0,                         // reject immediately, never queue
            },
            OnRejected = args =>
            {
                logger.LogWarning("Geo-IP concurrency limit ({Limit} in flight) rejected a call...");
                ResilienceMetrics.BulkheadRejections.Add(1, new("dependency", "geo-ip"));
                return default;
            },
        })
        .AddCircuitBreaker(new() { /* unchanged: FailureRatio=0.5, MinimumThroughput=10, ... */ });
});
```

```mermaid
flowchart LR
    click[Click event] --> resolver[IpApiGeoIpResolver.ResolveAsync]
    resolver --> limiter{Concurrency limiter<br/>PermitLimit=N, QueueLimit=0}
    limiter -- permit available --> breaker{Circuit breaker<br/>closed?}
    limiter -- no permit --> rejected[RateLimiterRejectedException]
    breaker -- yes --> http[HTTP call to ip-api.com]
    breaker -- open --> brokenCircuit[BrokenCircuitException]
    http --> result[GeoLocation or error]
    rejected --> catchAll[ResolveAsync catch-all]
    brokenCircuit --> catchAll
    result -- on throw --> catchAll
    catchAll --> noGeo["GeoLocation(null, null)<br/>click still recorded"]
```

- **Ordering is deliberate: the rate limiter is added *before* the circuit breaker in the same
  pipeline.** A call rejected by the concurrency limiter never reaches the breaker, so a burst of
  concurrency rejections never counts against the breaker's own `FailureRatio` — the two strategies
  protect against two independent failure modes (slow vs. down) and shouldn't contaminate each
  other's statistics. Reversing the order would let pure concurrency pressure trip the breaker open,
  with nothing enforcing the correct order beyond the sequence of two method calls.
- **`AddRateLimiter(RateLimiterStrategyOptions)`, not the simpler `AddConcurrencyLimiter` overload** —
  the simple overload only accepts the BCL's own `ConcurrencyLimiterOptions`, which has no rejection
  callback; the lower-level overload adds `OnRejected` for logging/metrics while keeping identical
  limiting behavior.
- **`QueueLimit = 0`** — reject immediately rather than queue excess calls, mirroring
  [RedirectApi](node:redirect-api)'s own request-budget load-shedding choice
  (`Redirect:RequestBudgetMs`): abandoning a call that can't start right now beats queuing it, since
  queuing risks this consumer's own RabbitMQ batch timeout.
- **No call-site change needed** — `Polly.RateLimiting.AddRateLimiter` throws
  `RateLimiterRejectedException` on rejection, just another `Exception` to `IpApiGeoIpResolver.
  ResolveAsync`'s existing catch-all, which already turns *any* failure (private IP, timeout, HTTP
  error, now also a rejection) into `GeoLocation(null, null)` rather than throw — a rejection and a
  genuinely dead dependency look identical downstream; only the OTel counters tell them apart
  (`resilience.bulkhead.rejections` vs. `resilience.circuit_breaker.transitions{state=opened}`).
- **Deliberately manual, not self-tuning** — `GeoIp:MaxConcurrentRequests` (default 10) is read once
  at startup and changed only by recreating both containers (via the `geoip-bulkhead` control-api
  capability or the `GEOIP_BULKHEAD_LIMIT` env var), not auto-adjusted from observed latency/rejection
  rate — a dial a human turns and observes, matching this project's visible-cause-and-effect teaching
  model over hidden automation.
- **Private/loopback/docker-internal IPs never reach the limiter** — `ResolveAsync` short-circuits to
  "no geo data" before the `HttpClient` (and the whole resilience pipeline) is even touched for any
  non-public IP, which matters when trying to reproduce rejections in a local docker-compose run
  where most client IPs are docker-internal.

## Turning it off

Every breaker above is **always on in production code**, but for an educational sandbox whose whole
point is to let someone *feel* the difference a pattern makes, that's a real gap: there was no way
to see the "without a circuit breaker" baseline live — only read about it. A live on/off switch
closes that gap: with it, stop a dependency (e.g. RabbitMQ), watch a breaker open and every
subsequent call fail fast, flip breakers off, repeat the same outage, and watch every call instead
pay the dependency's full timeout again before falling back to whatever fail-open behavior already
existed underneath (the SQLite queue, a cache miss, ...).

**A DI-time no-op swap, not an if/else in the call path** — matching this project's own standing
convention for behavior toggles. `ResiliencePipelines.BuildCircuitBreaker` takes a third parameter,
`bool enabled = true`; when `false`, it returns `ResiliencePipeline.Empty` (a real Polly pipeline
whose `ExecuteAsync` just invokes the callback directly) instead of building a breaker. This is
decided once, at service registration (`TryAddKeyedSingleton`), never re-checked per call:

```csharp
// src/backend/Shared/Common/ResiliencePipelines.cs (shape)
public static ResiliencePipeline BuildCircuitBreaker(string dependencyName, ILogger logger, bool enabled = true)
{
    if (!enabled)
    {
        return ResiliencePipeline.Empty; // every ExecuteAsync call just invokes the delegate directly
    }
    return new ResiliencePipelineBuilder().AddCircuitBreaker(/* ... */).Build();
}

// read once at startup, independently by each of the three call sites below
public static bool CircuitBreakersEnabled(IConfiguration configuration) =>
    configuration.GetValue("Resilience:CircuitBreakersEnabled", true);
```

The three `TryAddKeyedSingleton` call sites (`RedisExtensions.cs`, `GrpcMessagingExtensions.cs`,
`RabbitMqExtensions.cs`) all pass `ResiliencePipelines.CircuitBreakersEnabled(configuration)` as the
new argument. The ip-api.com breaker (built differently, via
`Microsoft.Extensions.Http.Resilience`'s `AddResilienceHandler`, not this shared builder — see "How
it's implemented here" above) can't return an empty pipeline the same way, so `GeoIpExtensions.cs`
instead just skips chaining `.AddCircuitBreaker(...)` onto that pipeline when the flag reads false;
the bulkhead concurrency limiter earlier in that same pipeline is a separate toggle and is
unaffected either way.

**The toggle recreates six containers, not one.** `InfraToggleService` tracks
`_circuitBreakersEnabled` as in-memory standing state (default `true`, lost on a control-api
restart like every other toggle here), exposed via the `circuit-breaker-toggle` capability
(`GET`/`POST /api/infra/circuit-breakers`) and `CIRCUIT_BREAKERS_ENABLED` in `CurrentStandingEnv()`.
Flipping it force-recreates every service that registers at least one breaker —
`auth-api`/`link-api`/`redirect-api`/`shortener-service`/`traffic-service`/`reporting-service` —
via `sandbox/docker-compose.yml`'s `Resilience__CircuitBreakersEnabled:
"${CIRCUIT_BREAKERS_ENABLED:-true}"` on each of those six. `reporting-api` is deliberately excluded:
it only touches Redis through `AddRedisMultiplexer` for SignalR pub/sub, which registers no breaker
of its own. The architecture-map control (`CircuitBreakerControl.tsx`, registered under the
`circuit-breaker-toggle` capability name on all six affected nodes) can be opened from any one of
them — there's exactly one shared flag, not six independent ones, so toggling from any node's panel
affects all six together.

```mermaid
flowchart LR
    UI["Any of the 6 nodes' panels\n(CircuitBreakerControl)"] -- "POST /api/infra/circuit-breakers" --> CAPI["control-api\nInfraToggleService"]
    CAPI -- "force-recreate, --no-deps,\nResilience__CircuitBreakersEnabled=false" --> Services["auth-api, link-api, redirect-api,\nshortener-service, traffic-service,\nreporting-service"]
    Services -- "CircuitBreakersEnabled(config) read\nonce at startup" --> Pipeline{"enabled?"}
    Pipeline -- "true" --> Breaker["real Polly breaker\n(FailureRatio/BreakDuration as above)"]
    Pipeline -- "false" --> Empty["ResiliencePipeline.Empty\n(every call just runs the delegate)"]
```

Because the flag is read once per process at startup, toggling it is "recreate six containers" (a
few seconds), not an instant in-process flip — the same "recreate, don't hot-reload" shape every
other `InfraToggleService` toggle already uses in this project. Disabling the breaker also doesn't
touch the ip-api.com pipeline's separate bulkhead/concurrency limiter (see "A related pattern" above)
— that stays active either way, since it's a different toggle covering a different failure mode.

## Pitfalls

No pitfall-docs recorded for this pattern yet — the tuning duplication between the shared builder
and ip-api.com's hand-copied `HttpClient` values (see "How it works") is a real drift risk worth
watching, but nothing has actually drifted yet, so there's no before/after to show. The same applies
to the rate-limiter-before-breaker ordering rule above: nothing enforces it beyond code review.

## Relatives

### Nodes

- [Redis](node:redis-master) — the shared breaker behind `EntityCacheService`/`CachingGeoIpResolver`
- [RabbitMQ](node:rabbitmq) — the breaker in front of `RentChannelAsync`
- [LinkApi](node:link-api) — Redis breaker (cache reads/writes), RabbitMQ breaker (publish)
- [RedirectApi](node:redirect-api) — Redis breaker (cache reads/writes), RabbitMQ breaker (publish)
- [ShortenerService](node:shortener-service) — gRPC breaker when `Messaging:Mode=grpc`
- [TrafficService](node:traffic-service) — gRPC breaker when `Messaging:Mode=grpc`; Redis breaker
  via `CachingGeoIpResolver`; geo-IP bulkhead (concurrency limiter) in front of the same ip-api.com
  calls
- [ReportingService](node:reporting-service) — Redis breaker via `CachingGeoIpResolver`; geo-IP
  bulkhead (concurrency limiter) in front of the same ip-api.com calls

### Patterns

- [Caching](pattern:caching) — the Redis breaker sits in front of this pattern's cache-aside calls
- [Async messaging](pattern:async-messaging) — the RabbitMQ and gRPC breakers both sit on this
  pattern's transports
