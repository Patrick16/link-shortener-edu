# RabbitMQ's publisher stack started even when the active transport doesn't use it

**Category:** performance **Status:** fixed

When a service gains a runtime toggle between two alternate transports, watch for startup code
that doesn't actually check which one is active. Every background worker, pooled connection, and
fallback mechanism that a transport brings along gets paid for at startup whether or not anything
will ever use it — wasted latency and resources that are easy to miss because nothing is
*broken*, the unused transport just quietly runs alongside the one actually doing the work.

**Concrete instance in this project:** `LinkApi`/`RedirectApi`'s `Program.cs` called
`builder.AddRabbitMqPublisher()` unconditionally, regardless of the new `Messaging:Mode` setting.
In `messaging-mode=grpc`, no controller ever resolves `ILocalPublishQueue`/`IMessagePublisher` —
the gRPC dispatcher doesn't use RabbitMQ at all — yet gRPC mode still paid for opening a RabbitMQ
connection, `RabbitMqWarmupService`'s blocking connection-open at startup, the `RabbitMqRetryWorker`
background loop, `LocalPublishQueueWorker`, and a SQLite fallback-store file, all for a transport
nothing would ever call.

⚠️ **Mistake** — reconstructed; no diffable pre-fix commit exists (this feature was reviewed and
fixed before its first commit — see
`.notes/review-reports/2026-10-06-1430-messaging-toggle-grpc-review.md`, F7):

```csharp
// src/backend/Services/LinkApi/Program.cs
builder.AddPostgresDbContextPool<DatabaseContext>();
builder.AddRedisDistributedCache();

builder.AddRabbitMqPublisher(); // runs even in messaging-mode=grpc, where nothing uses it
builder.AddEventDispatcher();
builder.AddLinkServices();
builder.AddJwtAndInternalApiKeyAuthentication();
builder.Services.AddFrontendCors(builder.Configuration);

var healthChecks = builder.Services.AddHealthChecks()
    .AddPostgresHealthCheck<DatabaseContext>()
    .AddRabbitMqHealthCheck(); // would fail to resolve IRabbitMqConnection once the publisher is gated
```

✅ **Do this instead** — gate both the publisher registration and its paired health check behind
the same mode check, so gRPC mode never starts a connection, warmup service, retry worker, or
fallback store it won't use:

```csharp
// src/backend/Services/LinkApi/Program.cs
builder.AddPostgresDbContextPool<DatabaseContext>();
builder.AddRedisDistributedCache();

var isGrpcMode = builder.Configuration.IsMessagingGrpcMode();
if (!isGrpcMode)
{
    builder.AddRabbitMqPublisher();
}
builder.AddEventDispatcher();
builder.AddLinkServices();
builder.AddJwtAndInternalApiKeyAuthentication();
builder.Services.AddFrontendCors(builder.Configuration);

var healthChecks = builder.Services.AddHealthChecks()
    .AddPostgresHealthCheck<DatabaseContext>();
if (!isGrpcMode)
{
    healthChecks.AddRabbitMqHealthCheck();
}
```

The health-check gate wasn't part of the original finding — it was caught while implementing the
fix: `AddRabbitMqHealthCheck()` would otherwise fail to resolve `IRabbitMqConnection` once the
publisher registration above it was skipped, a good example of how gating one registration can
surface a second, previously-invisible dependency on it. `RedirectApi/Program.cs` carries the
identical gate. Verified live: in gRPC mode, `link-api`'s logs contained zero mentions of
"rabbitmq" (no connection attempt, no health check registered); toggling back to rabbitmq mode
confirmed `RabbitMqPublisher` actually re-activates.

(see `.notes/review-reports/2026-10-06-1430-messaging-toggle-grpc-review.md`, F7 — fixed; no
pre-fix commit hash cited, reconstructed from the finding's own Summary plus the current code at
HEAD)

## Relatives

### Nodes

- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)

### Patterns

- [Async messaging](pattern:async-messaging) — the RabbitMQ connection/warmup/retry-worker/fallback
  stack this gate skips entirely in the alternate transport mode
