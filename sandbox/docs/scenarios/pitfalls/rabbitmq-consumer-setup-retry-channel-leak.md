# A consumer's channel-setup retry loop leaked a channel on every failed declare/bind attempt

**Category:** memory-leak **Status:** fixed

Watch for a retry loop that opens a resource first and only wires up its remaining setup steps
afterward: if a later step throws, the already-open resource from this attempt is easy to forget
about, since the `catch` block's job (log, back off, loop again) doesn't naturally touch it.
`RabbitMqConsumer.SetUpChannelWithRetryAsync` opens a channel, then runs
`ExchangeDeclareAsync`/`QueueDeclareAsync`/`QueueBindAsync`/`BasicQosAsync` on it before returning
it to the caller. If the channel opened fine but any one of those four follow-up calls threw (a
transient broker error mid-handshake, an exchange already declared with different arguments, a
broker that flaps — up just long enough to accept a channel open but not long enough for the rest
of the sequence), the original code's `catch` logged a warning and retried without ever disposing
the channel that *had* already opened — it simply fell out of scope on the next loop iteration.

During a sustained outage or a flapping broker, each retry round (every 2–30s per the exponential
backoff) could repeat this, opening and abandoning another channel every time. Channels accumulate
on the broker connection until RabbitMQ's `channel_max` is hit, after which even a healthy
`CreateChannelAsync` call starts failing outright — the consumer's own recovery path exhausting the
very resource it needs to recover.

⚠️ **Mistake** — `src/backend/Shared/Infrastructure/RabbitMqConsumer.cs`, `SetUpChannelWithRetryAsync`:

```csharp
var delay = InitialRetryDelay;
while (true)
{
    try
    {
        var channel = await _connection.CreateChannelAsync(cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(/* ... */);
        await channel.QueueDeclareAsync(queueName, /* ... */);
        await channel.QueueBindAsync(queueName, /* ... */);
        await channel.QueueDeclareAsync(deadQueueName, /* ... */);
        await channel.BasicQosAsync(0, prefetchCount: _prefetchCount, global: false, cancellationToken: cancellationToken);

        return channel;
    }
    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
    {
        _logger.LogWarning(ex, "Failed to set up RabbitMQ consumer for queue {QueueName}, retrying in {Delay}", queueName, delay);

        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxRetryDelay.TotalSeconds));
    }
}
```

✅ **Do this instead** — declare the channel variable outside the `try` so the `catch` block can see
and dispose it if it opened before a later step failed:

```csharp
var delay = InitialRetryDelay;
while (true)
{
    IChannel? channel = null;
    try
    {
        channel = await _connection.CreateChannelAsync(cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(/* ... */);
        await channel.QueueDeclareAsync(queueName, /* ... */);
        await channel.QueueBindAsync(queueName, /* ... */);
        await channel.QueueDeclareAsync(deadQueueName, /* ... */);
        await channel.BasicQosAsync(0, prefetchCount: _prefetchCount, global: false, cancellationToken: cancellationToken);

        return channel;
    }
    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
    {
        // The channel may have opened fine even though a later declare/bind/QoS call failed -
        // without disposing it here, every retry round (up to every 30s during a sustained
        // outage) leaks another open channel on the broker.
        if (channel is not null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        _logger.LogWarning(ex, "Failed to set up RabbitMQ consumer for queue {QueueName}, retrying in {Delay}", queueName, delay);

        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxRetryDelay.TotalSeconds));
    }
}
```

(see review-reports/2026-09-17-2021-ce8310d-rabbitmq-retry-migrations.md, F2 — fixed in `dd6ac13`)

## Relatives

### Nodes

- [ShortenerService](node:shortener-service) — the consumer-side channel-setup loop runs here
- [TrafficService](node:traffic-service) — and here (same shared `RabbitMqConsumer`)
- [RabbitMQ](node:rabbitmq)

### Patterns

- [Async messaging](pattern:async-messaging)
