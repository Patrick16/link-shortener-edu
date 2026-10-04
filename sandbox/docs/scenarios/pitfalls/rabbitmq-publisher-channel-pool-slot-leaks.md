# The publisher's channel pool could permanently shrink to zero on broker errors

**Category:** connection-pooling **Status:** fixed

`RabbitMqPublisher` pools already-open, already-exchange-declared channels behind a
`SemaphoreSlim` (`_slots`) so publishing doesn't have to open a channel and redeclare the exchange
on every call. Watch for a pool's slot-release logic that only runs on the happy path: two of this
pool's methods could leak a slot — permanently shrinking the pool's capacity by one — on realistic
broker-error paths, both introduced in the same commit that added the pool:

- `RentChannelAsync` disposed stale idle channels (found closed after a reconnect) *outside* the
  try/catch that releases the semaphore slot. If `DisposeAsync()` on one of them threw — a real
  possibility when the underlying connection was forcibly closed — the exception propagated out
  without ever calling `_slots.Release()`. Separately, if `CreateChannelAsync` succeeded but the
  following `ExchangeDeclareAsync` failed, the `catch` block released the slot but never disposed
  the channel that *had* opened — leaking it on the broker until `channel_max`/connection teardown.
- `ReleaseChannelAsync` called `_slots.Release()` as its last statement, not in a `finally`. If
  disposing an unhealthy channel threw, the slot was never released — and the exception also
  escaped `TryPublishAsync`'s own `finally`, bypassing the "always fall back to SQLite on failure"
  contract for that call, so the event was lost outright on top of losing a slot.

Either path repeating enough times (exactly what happens during a sustained reconnect, where
several pooled idle channels are found broken at once) empties the pool of its 16 slots; every
future `PublishAsync`/`TryRepublishAsync` call then blocks on `_slots.WaitAsync` forever, wedging
the publisher for the app's remaining lifetime — worse than the pre-pooling behavior of "just fail
this one publish and fall back to SQLite."

⚠️ **Mistake** — `src/backend/Shared/Infrastructure/RabbitMqPublisher.cs`:

```csharp
private async Task<IChannel> RentChannelAsync(CancellationToken cancellationToken)
{
    await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

    while (_idleChannels.TryDequeue(out var idle))
    {
        if (idle.IsOpen)
        {
            return idle;
        }

        await idle.DisposeAsync().ConfigureAwait(false);
    }

    try
    {
        var channel = await _connection.CreateChannelAsync(cancellationToken).ConfigureAwait(false);
        await channel.ExchangeDeclareAsync(
            MessagingConstants.EventsExchange, ExchangeType.Topic, durable: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return channel;
    }
    catch
    {
        _slots.Release();
        throw;
    }
}

private async Task ReleaseChannelAsync(IChannel channel, bool healthy)
{
    if (healthy && channel.IsOpen)
    {
        _idleChannels.Enqueue(channel);
    }
    else
    {
        await channel.DisposeAsync().ConfigureAwait(false);
    }

    _slots.Release();
}
```

✅ **Do this instead** — wrap the whole idle-dequeue-through-declare sequence in a try/catch that
always releases the slot; dispose an already-opened channel before rethrowing if the declare
fails; move `ReleaseChannelAsync`'s slot release into a `finally`:

```csharp
private async Task<IChannel> RentChannelAsync(CancellationToken cancellationToken)
{
    await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

    // Everything from here on holds a slot - any exception (including a stale idle channel failing
    // to dispose, or the exchange declare below) must release it before propagating, or the pool
    // permanently loses capacity and every publish eventually blocks forever.
    try
    {
        while (_idleChannels.TryDequeue(out var idle))
        {
            if (idle.IsOpen)
            {
                return idle;
            }

            await idle.DisposeAsync().ConfigureAwait(false);
        }

        IChannel? channel = null;
        try
        {
            channel = await _connection.CreateChannelAsync(cancellationToken).ConfigureAwait(false);
            await channel.ExchangeDeclareAsync(
                MessagingConstants.EventsExchange, ExchangeType.Topic, durable: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return channel;
        }
        catch
        {
            // The channel opened fine but the declare failed (or was cancelled) - without this, the
            // open channel is never disposed and leaks on the broker until channel_max is exhausted.
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }
    catch
    {
        _slots.Release();
        throw;
    }
}

private async Task ReleaseChannelAsync(IChannel channel, bool healthy)
{
    try
    {
        if (healthy && channel.IsOpen)
        {
            _idleChannels.Enqueue(channel);
        }
        else
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }
    finally
    {
        // Must run even if DisposeAsync above throws - otherwise the slot is lost permanently, and
        // the exception would also escape TryPublishAsync's own finally, skipping the
        // SQLite-fallback path entirely instead of just failing this one publish.
        _slots.Release();
    }
}
```

(fixed in `dd6ac13`)

## Relatives

### Nodes

- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)

### Patterns

- [Async messaging](pattern:async-messaging)
