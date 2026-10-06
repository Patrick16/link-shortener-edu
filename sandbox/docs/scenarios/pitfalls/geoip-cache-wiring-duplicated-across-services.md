# Shared-cache DI wiring copy-pasted across two independently deployed services

**Category:** coupling **Status:** fixed

Watch for this once two independently deployed services need to agree on *how* they talk to a
shared piece of infrastructure — not just that they both talk to it. A Redis-backed cache only
actually dedupes work between two consumers if both register it identically: same instance
address, same `Redis:InstanceName` (so key prefixes line up), same wrapping class. If that
registration is copy-pasted into each service's own startup code instead of factored into one
shared place, nothing stops the two copies from drifting the next time either one is touched —
and the failure mode is silent: the services still start, still run, the cache just quietly stops
being shared, and the exact problem it was built to fix (see the companion pitfall below) comes
back without anyone changing the behavior they meant to.

**Concrete instance in this project:** once `CachingGeoIpResolver` was introduced to let
`TrafficService` and `ReportingService` share one Redis-backed geo-IP cache instead of each hitting
`ip-api.com` independently, the registration block (wire up `IDistributedCache`, an `HttpClient`
for `IpApiGeoIpResolver`, and the `CachingGeoIpResolver` wrapper around it) was duplicated
near-identically in both `TrafficServiceExtensions.cs` and `ReportingServiceExtensions.cs`, each
with a comment admitting it had to be kept in sync with the other by hand for the shared cache to
actually work.

⚠️ **Mistake** — reconstructed (no diffable pre-fix commit; this feature was reviewed and fixed
before its first commit — see
`.notes/review-reports/2026-10-06-1430-messaging-toggle-grpc-review.md`, F9). The same block,
independently present in both services' extension classes:

```csharp
// TrafficServiceExtensions.cs AND, separately, ReportingServiceExtensions.cs -
// identical by hand, nothing enforcing it stays that way.
builder.AddRedisDistributedCache();
builder.Services.AddHttpClient<IpApiGeoIpResolver>(client =>
{
    client.BaseAddress = new Uri("http://ip-api.com");
});
builder.Services.AddSingleton<IGeoIpResolver>(sp => new CachingGeoIpResolver(
    sp.GetRequiredService<IpApiGeoIpResolver>(),
    sp.GetRequiredService<IDistributedCache>(),
    sp.GetRequiredService<ILogger<CachingGeoIpResolver>>()));
```

✅ **Do this instead** — one shared extension method, `src/backend/Shared/Infrastructure/GeoIpExtensions.cs`,
called identically by both services instead of each owning its own copy:

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

```csharp
// TrafficServiceExtensions.cs
builder.AddGeoIpResolution();
```

```csharp
// ReportingServiceExtensions.cs
builder.AddGeoIpResolution();
```

A shared library can't enforce this by itself — `GeoIpExtensions` living in `Shared/Infrastructure`
only helps because both services actually call it instead of rolling their own. The lesson is the
same shape as [this project's `Common`/Redis-client coupling finding](pitfall:common-lib-full-redis-client-dependency),
just in the other direction: that one was about a shared lib pulling in *too much*, this one is
about two services independently reinventing wiring that should have been shared from the start.

(see `.notes/review-reports/2026-10-06-1430-messaging-toggle-grpc-review.md`, F9 — fixed; no
pre-fix commit hash cited, reconstructed from the finding's own Summary plus the current code at
HEAD)

## Relatives

### Nodes

- [TrafficService](node:traffic-service)

### Patterns

- [Caching](pattern:caching) — the shared cache this wiring backs
