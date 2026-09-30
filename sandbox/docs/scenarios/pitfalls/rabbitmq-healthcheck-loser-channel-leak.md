# RabbitMQ health check leaked a channel when its own timeout won the race

**Category:** memory-leak **Status:** fixed

`RabbitMqHealthCheck` races `connection.CreateChannelAsync(...)` against a 3-second
`Task.Delay` via `Task.WhenAny` — necessary because cancelling the passed-in token alone doesn't
bound a wait on the *shared* connection-establishment task other callers started (RabbitMQ.Client
doesn't reliably abort that handshake just because one caller's token fired; a real 200-VU run
confirmed this taking 9-15s+ instead of the intended 3s). But the first version of that race simply
returned `Unhealthy` the moment the timeout won, without ever touching `channelTask` again. If that
task *later completed successfully* — exactly the scenario the race exists to tolerate, a slow but
eventually-successful connection — it produced a live `IChannel` that nothing ever disposed: one
more open channel on the shared connection (client-side state plus a slot in the broker's per-
connection channel table), accumulating on every Docker healthcheck cycle (`interval: 5s`) for as
long as RabbitMQ stayed slow.

🐛 **Bug** — abandons the loser task instead of observing it:

```csharp
var channelTask = connection.CreateChannelAsync(cancellationToken);
var winner = await Task.WhenAny(channelTask, Task.Delay(Timeout, cancellationToken));

if (winner != channelTask)
{
    return HealthCheckResult.Unhealthy($"RabbitMQ did not respond within {Timeout.TotalSeconds}s.");
}

await using var channel = await channelTask;
return HealthCheckResult.Healthy();
```

✅ **Fix** — attaches a continuation to the loser that disposes the channel if it eventually
succeeds, or observes the fault if it doesn't, before returning `Unhealthy`:

```csharp
var channelTask = connection.CreateChannelAsync(cancellationToken);
var winner = await Task.WhenAny(channelTask, Task.Delay(Timeout, cancellationToken));

if (winner != channelTask)
{
    // Don't abandon the loser: if it eventually succeeds, it hands back a live IChannel
    // that nothing else will ever dispose (a leaked client-side channel plus a slot in the
    // broker's per-connection channel table) - observe it instead, on whichever thread the
    // continuation runs on, well after this method has already returned Unhealthy.
    _ = channelTask.ContinueWith(
        static t =>
        {
            if (t.IsCompletedSuccessfully)
            {
                return DisposeQuietlyAsync(t.Result);
            }

            _ = t.Exception; // observe the fault so it never surfaces as unobserved later
            return Task.CompletedTask;
        },
        TaskScheduler.Default).Unwrap();
    return HealthCheckResult.Unhealthy($"RabbitMQ did not respond within {Timeout.TotalSeconds}s.");
}

await using var channel = await channelTask;
return HealthCheckResult.Healthy();
```

`DisposeQuietlyAsync` guards the disposal itself the same way the failure branch observes its own
fault — an already-abandoned channel throwing from `DisposeAsync()` would otherwise surface as an
unobserved task exception with nothing left able to act on it:

```csharp
private static async Task DisposeQuietlyAsync(IChannel channel)
{
    try
    {
        await channel.DisposeAsync();
    }
    catch
    {
    }
}
```

This bug never existed as a separate committed state — the review ran against the uncommitted
working tree of a live performance-debugging session (chasing a ~20s `/health/ready` stall), and
the fix was applied before the commit landed. The before/above is reconstructed from the finding's
own description of the code, not a real diff; the report's cited resolution hash (`67f7587`) turns
out not to touch this file at all (confirmed via `git log`) — the actual fix landed together with
the race-condition rewrite itself in commit `e89e26e`.

(see review-reports/2026-09-29-1238-rabbitmq-healthcheck-pgcat-fix.md, F1)

## Relatives

### Nodes

- [LinkApi](node:link-api) — publisher, depends on the same shared `IRabbitMqConnection`
- [RedirectApi](node:redirect-api) — publisher, depends on the same shared `IRabbitMqConnection`
- [ShortenerService](node:shortener-service) — consumer, depends on the same shared `IRabbitMqConnection`

### Patterns

- [Async messaging](pattern:async-messaging) — the health check exists to detect a broken
  connection for this pattern's broker
