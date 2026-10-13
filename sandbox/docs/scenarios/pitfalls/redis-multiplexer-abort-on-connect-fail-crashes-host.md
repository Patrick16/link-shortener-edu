# A Redis blip at boot crashed the whole API

**Category:** resilience-gap **Status:** fixed

A dependency that is only needed for part of a service's work should not decide whether the whole
service starts. The default connect behaviour of the Redis client treats an unreachable server as a
fatal error for the process, so anything that resolves the connection during host startup takes the
process down with it.

**Concrete instance in this project:** ReportingApi serves two independent things: ClickHouse-backed
report endpoints and the Redis-backed live top-links view. It registered the Redis multiplexer as a
singleton, and that singleton is resolved while the host starts. When Sentinel was not yet reachable
(it starts in parallel with the API), `ConnectionMultiplexer.Connect` threw, and the process exited.
ReportingApi crashed with a `RedisConnectionException` on the first start attempt, even though its
report endpoints did not need Redis at all.

Mistake: connecting with the default options (`AbortOnConnectFail = true`) and letting the exception
escape during startup.

```csharp
// RedisExtensions.AddRedisMultiplexer (bd47119)
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connectionString!));
```

Do this instead (a2b405e, `src/backend/Shared/Infrastructure/RedisExtensions.cs:43-44`):

```csharp
var options = ConfigurationOptions.Parse(connectionString!);
options.AbortOnConnectFail = false;   // keep retrying in the background
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options));
```

The multiplexer now keeps retrying in the background. The same fix also needed a second change. The first
`SubscribeAsync` in `TopLinksPushService.ExecuteAsync` could still throw, and an exception from a
`BackgroundService` stops the host by default (`BackgroundServiceExceptionBehavior.StopHost`). That first
subscribe is now retried with exponential backoff, from 1 s up to 30 s (`TopLinksPushService.cs:55-80`).
After the first success, StackExchange.Redis re-subscribes on its own after a reconnect, so only the first
call needs the loop.

**Provenance:** both the before and the after are real commits (bd47119 and a2b405e).
**Reproduced live:** the connect path. ReportingApi exited at startup with a `RedisConnectionException`
thrown from `Program.cs` (exit code 139) while Sentinel was not ready.
**Not reproduced, code-path only:** the StopHost behaviour on a failed first subscribe, and the backoff
loop under a real outage.

## Relatives

### Nodes

- [ReportingApi](node:reporting-api) — the host this crashed at boot
- [Redis](node:redis-master) — the dependency whose unavailability at boot took the host down
- [Redis Sentinel](node:redis-sentinel-1) — the endpoint that was not yet reachable during parallel startup

### Patterns

- [High availability and failover](pattern:high-availability-failover) — a service must tolerate its HA dependency being mid-failover
