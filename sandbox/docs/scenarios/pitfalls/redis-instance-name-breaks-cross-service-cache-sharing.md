# Three services ran identical caching code against three disjoint Redis keyspaces

**Category:** coupling **Status:** fixed

Watch for this whenever two or more services are meant to share one Redis-backed cache via
`IDistributedCache`: running the exact same key-building code in each service is not enough on its
own. `Microsoft.Extensions.Caching.StackExchangeRedis`'s `RedisCache` prepends each service's own
configured `InstanceName` to every key before it ever reaches Redis — two services with different
`InstanceName` values write to two entirely different physical keyspaces, even calling an identical
shared `Key(id) => $"link:{id}"` helper. Nothing errors, nothing logs a mismatch; the cache simply
never gets a cross-service hit, silently, indefinitely.

**Concrete instance in this project:** `LinkApi`, `RedirectApi`, and `ShortenerService` all share
`LinkCacheService` (`Shared/Common/LinkCacheService.cs`) — `LinkApi` and `ShortenerService` are
meant to populate a `Link` entry, `RedirectApi` is meant to read it. Each
service's own `appsettings.json` set `Redis:InstanceName` to its own service name
(`"LinkApi"`/`"RedirectApi"`/`"ShortenerService"`) — nobody had reason to look twice at it, since
each value reads as a reasonable, service-scoped default in isolation. The result: `ShortenerService`'s
pre-warm write (`LinkCreatedConsumer.cs`, already in place before this was found — its own comment
claims it prevents exactly this) landed at `ShortenerServicelink:{hash}`; `RedirectApi`'s read
checked `RedirectApilink:{hash}`. Three different physical keys for what the code everywhere else
assumed was one shared entry.

**Found while verifying a different fix, not by inspection.** The
[PgCat read-your-writes race](pitfall:pgcat-read-your-writes-race) fix — `LinkApi` writing an
optimistic, short-TTL cache entry on create so `RedirectApi`'s immediate-redirect read never
touches Postgres — was implemented, unit-tested, and looked correct by every static check
(`dotnet build`, `dotnet test`, code review). It still 404ed on a real create → immediate-redirect
request against the live stack. Only checking the actual Redis keys directly
(`redis-cli KEYS "*<hash>*"`) surfaced three differently-prefixed entries instead of one — this bug
predates that fix entirely (`ShortenerService`'s pre-warm never worked either, for the same reason)
and would have stayed invisible without a live create→redirect check, since every *other* cache
behavior in this project (a second redirect of an already-cached link, hitting `RedirectApi`'s own
previously-written key) still worked fine. Ordinary cache-hit-rate testing never exercises the
specific cross-service path this broke.

⚠️ **Mistake** — each service's own `appsettings.json`:

```json
// Services/LinkApi/appsettings.json
"Redis": { "InstanceName": "LinkApi" }

// Services/RedirectApi/appsettings.json
"Redis": { "InstanceName": "RedirectApi" }

// Services/ShortenerService/appsettings.json
"Redis": { "InstanceName": "ShortenerService" }
```

✅ **Do this instead** — any services meant to share a cache's keyspace use the same `InstanceName`
(here, empty — no prefix needed since this project runs one Redis per environment, not a
multi-tenant shared instance):

```json
"Redis": { "InstanceName": "" }
```

...in all three of `Services/LinkApi/appsettings.json`, `Services/RedirectApi/appsettings.json`,
and `Services/ShortenerService/appsettings.json`. `AddRedisDistributedCache`
(`Shared/Infrastructure/RedisExtensions.cs`) now carries a comment calling this out explicitly, the
same way `TrafficService`/`ReportingService` already document their own deliberately-shared
`"GeoIpCache"` `InstanceName` for an unrelated cache (see the companion pitfall below) — that
precedent existed in this codebase already; it just wasn't applied here too.

**The general lesson, not just this specific fix:** a cache being "shared" between services is a
property of their *configuration* agreeing, not of their *code* being identical or even centralized
in one shared class. `LinkCacheService` living in `Shared/Common` already prevented the key-*format*
from drifting; it did nothing to prevent the key-*prefix* from drifting, because `InstanceName`
lives in each service's own config, read independently at each service's own startup. A shared
helper class can't enforce agreement on a setting it never sees.

## Relatives

### Nodes

- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)
- [ShortenerService](node:shortener-service)
- [Redis (master)](node:redis-master)

### Patterns

- [Caching](pattern:caching)

### Pitfalls

- [A read immediately after a write can land on a lagging replica](pitfall:pgcat-read-your-writes-race)
  — the fix this bug was found while verifying
- [Shared-cache DI wiring copy-pasted across two independently deployed services](pitfall:geoip-cache-wiring-duplicated-across-services)
  — the same "services must agree on cache config, not just cache code" lesson, in the DI-wiring
  shape instead of the InstanceName shape
