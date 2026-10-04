# Shared `Common` library pulled in the full Redis client just for one interface

**Category:** coupling **Status:** fixed

Watch for a shared library that references a *concrete* client package when it only actually needs
the small abstraction interface that client implements. `EntityCacheService` (in the `Common`
library, shared by every backend service) only needs `IDistributedCache`/
`DistributedCacheEntryOptions` — both live in the small `Microsoft.Extensions.Caching.Abstractions`
package. `Common.csproj` instead referenced
`Microsoft.Extensions.Caching.StackExchangeRedis`, the concrete Redis-backed implementation, which
drags in the full `StackExchange.Redis` client and its transitive dependencies. Every service that
references `Common` — including ones with no reason to talk to Redis at all — transitively carried
that client. Not a runtime bug (nothing actually broke), but unnecessary coupling: the dependency
didn't even buy `Common` anything beyond the interface, since each service using Redis for real
(`LinkApi`, and — once fixed — `RedirectApi`) still had to register `AddStackExchangeRedisCache`
itself in its own `Program.cs` either way. In a multi-service system this is exactly the kind of
boundary choice that increases blast radius later: a shared lib pulling in a concrete dependency
nobody downstream actually asked for means every service that merely references the lib is now
one step closer to being forced to upgrade/patch/audit a client it never chose to depend on.

⚠️ **Mistake** — `src/backend/Shared/Common/Common.csproj`:

```xml
<ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Caching.StackExchangeRedis" Version="10.0.12" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions" Version="10.0.0" />
    <PackageReference Include="MongoDB.Bson" Version="3.12.0" />
    <PackageReference Include="UAParser" Version="3.1.47" />
</ItemGroup>
```

✅ **Do this instead** — swap the concrete Redis package for the lightweight abstractions package
(plus an explicit `Logging.Abstractions` reference, which had been arriving transitively through
the Redis package and needed to be named directly once that path was gone). `RedirectApi.csproj` —
which had been building only because it inherited the Redis client transitively through `Common` —
now references `Microsoft.Extensions.Caching.StackExchangeRedis` directly instead, alongside its
existing `AddStackExchangeRedisCache` call in `Program.cs` (`LinkApi.csproj` already referenced it
directly, so it needed no change):

```xml
<!-- src/backend/Shared/Common/Common.csproj -->
<ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Caching.Abstractions" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
    <PackageReference Include="MongoDB.Bson" Version="3.12.0" />
    <PackageReference Include="UAParser" Version="3.1.47" />
</ItemGroup>
```

```xml
<!-- src/backend/Services/RedirectApi/RedirectApi.csproj -->
<ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.12" />
    <PackageReference Include="Microsoft.Extensions.Caching.StackExchangeRedis" Version="10.0.12" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
    ...
</ItemGroup>
```

The finding was logged against commit `3674b1b` (the commit that first introduced
`EntityCacheService` and this dependency), but the fix itself didn't land until much later, bundled
into commit `3c79e80` — a large, unrelated grab-bag commit (frontend test additions, a `Sault`→`Salt`
column-rename migration, a refresh-token cleanup worker, internal API-key auth) that happened to
also carry this one `.csproj` hunk. The two snippets above are that isolated hunk, not the full
commit diff.

(fixed in `3c79e80`)

## Relatives

### Nodes

- [Redis](node:redis-master) — the store `EntityCacheService`/`IDistributedCache` front
- [LinkApi](node:link-api) — already referenced the concrete Redis package directly, unaffected
- [RedirectApi](node:redirect-api) — gained its own direct Redis package reference by this fix

### Patterns

- [Caching](pattern:caching) — `EntityCacheService` is the pattern's generic implementation; this
  finding is about its packaging, not its cache-aside logic
