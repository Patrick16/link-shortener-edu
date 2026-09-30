# Shutting down the publisher only disposed idle channels, not ones still in flight

**Category:** memory-leak **Status:** fixed

`RabbitMqPublisher` is registered as a singleton and its `DisposeAsync()` runs on graceful host
shutdown. It only drained `_idleChannels` — the pool of channels currently sitting idle, ready to be
reused. Any channel actively rented out by an in-flight `PublishAsync`/`TryRepublishAsync` call
wasn't tracked anywhere else. If that call's own `ReleaseChannelAsync` happened to run *after*
`DisposeAsync` had already returned, it would enqueue the channel into `_idleChannels` — a queue
nothing would ever drain again, since disposal had already finished. That channel (and its AMQP
resources) leaked for the rest of the process's life. Low real-world impact — the process is
exiting anyway, so the OS reclaims the socket — but `IAsyncDisposable` didn't actually deliver on
its contract.

🐛 **Bug** — `src/backend/Shared/Infrastructure/RabbitMqPublisher.cs`, `DisposeAsync`:

```csharp
public async ValueTask DisposeAsync()
{
    while (_idleChannels.TryDequeue(out var channel))
    {
        await channel.DisposeAsync().ConfigureAwait(false);
    }
}
```

✅ **Fix** — track every rented-out channel in `_outstandingChannels`, and give `DisposeAsync`
and `ReleaseChannelAsync` a shared lock to agree, per channel, on exactly one of three
mutually-exclusive outcomes: still outstanding at release time (normal path), already claimed by a
concurrent `DisposeAsync` (release does nothing further), or already released into `_idleChannels`
before `DisposeAsync`'s snapshot ran (still caught by its idle-drain loop). Without that shared
ownership check, a naive "also track and dispose outstanding channels" fix would let a channel be
disposed twice — once by a racing `ReleaseChannelAsync`, once by `DisposeAsync`'s own loop:

```csharp
private readonly HashSet<IChannel> _outstandingChannels = [];
private readonly Lock _disposeLock = new();
private bool _disposed;

private void TrackOutstanding(IChannel channel)
{
    lock (_disposeLock)
    {
        _outstandingChannels.Add(channel);
    }
}
// ... RentChannelAsync calls TrackOutstanding(channel) on every path before returning it

private async Task ReleaseChannelAsync(IChannel channel, bool healthy)
{
    try
    {
        bool shouldDispose = false;
        lock (_disposeLock)
        {
            var stillOutstanding = _outstandingChannels.Remove(channel);
            if (stillOutstanding)
            {
                if (!_disposed && healthy && channel.IsOpen)
                {
                    _idleChannels.Enqueue(channel);
                }
                else
                {
                    shouldDispose = true;
                }
            }
            // else: DisposeAsync's snapshot already removed it and owns disposing it - do nothing.
        }

        if (shouldDispose)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }
    finally
    {
        _slots.Release();
    }
}

public async ValueTask DisposeAsync()
{
    List<IChannel> toDispose;
    lock (_disposeLock)
    {
        _disposed = true;
        toDispose = [.. _outstandingChannels];
        _outstandingChannels.Clear();
    }

    while (_idleChannels.TryDequeue(out var idle))
    {
        toDispose.Add(idle);
    }

    foreach (var channel in toDispose)
    {
        await channel.DisposeAsync().ConfigureAwait(false);
    }
}
```

Verified with a dedicated regression test using two `TaskCompletionSource`s to deterministically
hold a channel rented-out (not idle) while `DisposeAsync` runs concurrently, then release the
in-flight publish and assert the channel is disposed exactly once — confirmed to actually catch the
bug by temporarily reverting `DisposeAsync` to its "drain `_idleChannels` only" body and re-running
the test (it failed: the channel was never disposed at all).

The finding was logged against commit `57945c6` (which introduced the channel pool), but the fix
wasn't applied until later, bundled into commit `3c79e80` — a large, unrelated grab-bag commit
(frontend test additions, a `Sault`→`Salt` migration, a refresh-token cleanup worker, internal
API-key auth). The snippets above are the isolated hunk, not the full commit diff.

(see review-reports/2026-09-25-2109-57945c6-rabbitmq-channel-pooling.md, F3 — fixed in `3c79e80`)

## Relatives

### Nodes

- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)

### Patterns

- [Async messaging](pattern:async-messaging)
