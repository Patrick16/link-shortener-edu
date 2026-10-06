# Two independent event consumers doubled load on a shared rate-limited external API

**Category:** architecture-bug **Status:** fixed

Watch for this any time a second fan-out consumer gets added to an event that an existing consumer
already processes: if both independently call the same external dependency for the same event,
nothing about the messaging layer stops the new consumer from silently doubling (or worse,
n-tupling) load on that dependency — RabbitMQ topic exchanges are explicitly designed to deliver
one message to every bound queue, so this isn't a bug in the broker, it's a missing coordination
step between the two consumers. When the shared dependency is itself rate-limited, the effect
isn't just "twice the cost" — it can push both consumers over a limit that either one alone would
have stayed under, degrading a previously-working feature as a side effect of adding an unrelated
one.

**Concrete instance in this project:** `TrafficService.ClickTrackedConsumer` already resolved
geo-IP via `ip-api.com`'s free tier (~45 requests/minute) for every click. Adding
`ReportingService.ClickTrackedConsumer` as a second, independent fan-out consumer of the same
`ClickTrackedEvent` (for the new ClickHouse-backed CQRS read side) gave it its own direct call to
the same endpoint, with no coordination between the two. Total external call volume roughly
doubled, which was enough to trip the rate limit under real traffic — causing `ResolveAsync` to
return null/empty geo data in *both* services, not just the new one. The new feature's own geo
data being occasionally empty would have been an acceptable v1 limitation; silently degrading
TrafficService's previously-working geo accuracy was the real cost of not noticing the shared
budget.

⚠️ **Mistake** — reconstructed; no diffable pre-fix commit exists for this exact state (the
feature was reviewed and fixed before being committed, so there's no separate buggy commit to
`git show` — see `.notes/review-reports/2026-10-05-2356-cqrs-reporting-clickhouse.md`, F1). This
mirrors what `ReportingServiceExtensions.AddReportIngestion` originally did, duplicating
`TrafficServiceExtensions`'s own pre-existing direct registration with no shared cache between
them:

```csharp
public static WebApplicationBuilder AddReportIngestion(this WebApplicationBuilder builder)
{
    builder.Services.AddSingleton<IUserAgentParser, UaParserUserAgentParser>();

    // Independent of TrafficService's identical registration - no coordination, no shared cache.
    builder.Services.AddHttpClient<IpApiGeoIpResolver>(client =>
    {
        client.BaseAddress = new Uri("http://ip-api.com");
    });
    builder.Services.AddSingleton<IGeoIpResolver, IpApiGeoIpResolver>();

    builder.Services.AddHostedService<ClickTrackedConsumer>();
    return builder;
}
```

✅ **Do this instead** — `src/backend/Shared/Infrastructure/GeoIpExtensions.cs`: a new
`CachingGeoIpResolver` wraps the real resolver in a Redis-backed cache keyed by IP (10-minute TTL,
negative results cached too), and both services call the *same* registration helper so they end up
pointed at the same Redis instance/instance-name — whichever of the two near-simultaneous
deliveries for the same click resolves first makes the real API call, the other gets a cache hit
instead of a second call:

```csharp
public static IHostApplicationBuilder AddGeoIpResolution(this IHostApplicationBuilder builder)
{
    builder.AddRedisDistributedCache();
    builder.Services.AddHttpClient<IpApiGeoIpResolver>(client =>
    {
        client.BaseAddress = new Uri("http://ip-api.com");
    });
    builder.Services.AddSingleton<IGeoIpResolver>(sp => new CachingGeoIpResolver(
        sp.GetRequiredService<IpApiGeoIpResolver>(),
        sp.GetRequiredService<IDistributedCache>(),
        sp.GetRequiredService<ILogger<CachingGeoIpResolver>>()));

    return builder;
}
```

Both `TrafficServiceExtensions.AddClickTracking` and `ReportingServiceExtensions.AddReportIngestion`
now just call `builder.AddGeoIpResolution()` — this consolidation of what was originally two
separate copies of the wrapping code is itself a later, related fix, not part of this one; see
[Shared-cache DI wiring copy-pasted across two independently deployed services](pitfall:geoip-cache-wiring-duplicated-across-services).

(see `.notes/review-reports/2026-10-05-2356-cqrs-reporting-clickhouse.md`, F1 — fixed; no
pre-fix commit hash cited, reconstructed from the finding's own Summary plus the current code at
HEAD, per the usual caveat when a feature is reviewed and fixed before its first commit)

## Relatives

### Nodes

- [TrafficService](node:traffic-service) — the pre-existing consumer whose geo accuracy regressed
- [Redis](node:redis-master) — backs the shared cache that fixed this

### Patterns

- [Async messaging](pattern:async-messaging) — the fan-out (one event, multiple independent
  consumers) that made the duplicate work possible in the first place
- [Caching](pattern:caching) — the fix is a cache-aside layer shared between the two consumers
