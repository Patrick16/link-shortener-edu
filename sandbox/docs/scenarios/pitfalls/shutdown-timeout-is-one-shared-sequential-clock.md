# `HostOptions.ShutdownTimeout` is one shared clock, not a budget per hosted service

**Category:** config-tuning **Status:** fixed

Watch for this any time a .NET Generic Host app sets `HostOptions.ShutdownTimeout` and has more
than one `IHostedService` — including Kestrel's own hosting service, which every ASP.NET Core app
already has whether or not it's visible in `Program.cs`. It's easy to read "shutdown timeout" as
"each hosted service gets this long to stop" when it's actually one shared, sequential clock that
every `StopAsync` call draws down together, in a specific, non-configurable order.

**Concrete instance in this project:** `WebApplicationBuilder.Build()` registers Kestrel's own
hosting service (`GenericWebHostService`) **last**, and the .NET Generic Host stops hosted services
in **reverse registration order** — so on shutdown, Kestrel's own connection draining (stop
accepting new connections, let in-flight ones finish) always runs **first**, and only once that
completes do the app's own earlier-registered hosted services — here, `LocalPublishQueueWorker` and
`RabbitMqRetryWorker` — get whatever time is **left** of the shared `ShutdownTimeout` to drain their
own queues. A slow in-flight HTTP request could in principle leave little of the budget for those
workers to drain a nonempty backlog by the time their own `StopAsync` runs. This isn't a regression
against anything that existed before — there simply was no enforced shutdown budget at all before
this feature — but a comment describing the mechanism has to get the order right, or a future
reader will assume an independent-per-service budget that doesn't exist.

Pairs with a second, Docker-side setting that has to stay in sync with it: Compose's own
`stop_grace_period` defaults to 10s, while ASP.NET Core's Generic Host defaults
`HostOptions.ShutdownTimeout` to 30s — two unrelated frameworks' defaults, never set explicitly
together before this feature, silently fighting each other. `docker compose stop` would send
SIGTERM, wait Compose's 10s, then SIGKILL — three seconds before Kestrel's own drain was even a
third of the way through its 30s allowance. Fixed by making both explicit and paired: `
HostOptions.ShutdownTimeout = 15s` (via a new `AddGracefulShutdown()` extension, called only from
`LinkApi`/`RedirectApi`) and `stop_grace_period: 20s` on those same two Compose service blocks — the
5-second margin covers the process actually finishing its exit once the app's own budget elapses.

⚠️ **Mistake** — the first commit's own code comments (both in `WebApiDefaultsExtensions.cs`'s
`AddGracefulShutdown` and in `LocalPublishQueueWorker.cs`'s `StopAsync`) described the 15 seconds as
an independent allowance for each hosted service, not a shared sequential clock Kestrel's own drain
consumes first:

```csharp
// (comment, before the fix) Gives the host 15 seconds to shut down gracefully, including
// draining any in-flight work in LocalPublishQueueWorker/RabbitMqRetryWorker.
public static WebApplicationBuilder AddGracefulShutdown(this WebApplicationBuilder builder)
{
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));
    return builder;
}
```

✅ **Do this instead** (`a96d3f1`) — correct both comments to state the shared-sequential-clock
behavior explicitly and cross-reference each other, with no code change at all (the setting itself
was already correct; only its documentation was wrong):

```csharp
// HostOptions.ShutdownTimeout is ONE shared, sequential clock across every IHostedService's
// StopAsync during host shutdown, not an independent budget per service. WebApplicationBuilder
// registers Kestrel's own hosting service LAST, and the Generic Host stops services in reverse
// registration order - so Kestrel's own connection draining always runs FIRST, and
// LocalPublishQueueWorker/RabbitMqRetryWorker (registered earlier) only get whatever is left of
// this budget once that finishes. See LocalPublishQueueWorker.StopAsync's own comment.
public static WebApplicationBuilder AddGracefulShutdown(this WebApplicationBuilder builder)
{
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));
    return builder;
}
```

This matters for how a reader reasons about the system, not just for comment accuracy — a slow
in-flight HTTP request genuinely can starve the fallback-queue workers of their share of the drain
window, which is the opposite of what "15 seconds for each service" would lead you to expect.

**Provenance:** real commits `00a621e` (the `stop_grace_period`/`ShutdownTimeout` pairing) and
`a96d3f1` (the comment correction, confirmed empirically during `feature-review` rather than just
asserted).

## Relatives

### Nodes

- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)

### Patterns

- [Async messaging](pattern:async-messaging) — `LocalPublishQueueWorker`/`RabbitMqRetryWorker`,
  the hosted services competing for whatever's left of the shared shutdown clock
