using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Infrastructure;

public sealed class RabbitMqPublisher(
    IRabbitMqConnection connection,
    IMessageFallbackStore fallbackStore,
    ILogger<RabbitMqPublisher> logger) : IMessagePublisher, IAsyncDisposable
{
    private readonly IRabbitMqConnection _connection = connection;
    private readonly IMessageFallbackStore _fallbackStore = fallbackStore;
    private readonly ILogger<RabbitMqPublisher> _logger = logger;

    // Bounded pool of already-open, already-exchange-declared channels, reused across publishes
    // instead of opening a channel + re-declaring the exchange + closing the channel on every single
    // call - that per-request round trip (3 AMQP frames minimum) turned out to be the actual
    // throughput ceiling under load, not Postgres/PgCat: profiled redirect-api pinned at 90-120% CPU
    // per replica while Postgres sat at ~33% and PgCat at ~38%, and RabbitMQ's own channel_created
    // counter tracked 1:1 with the request count. 16 is a fixed, generous cap for this workload (a
    // handful of tiny publishes per request), not something worth exposing as a config knob.
    private const int PoolCapacity = 16;
    private readonly SemaphoreSlim _slots = new(PoolCapacity, PoolCapacity);
    private readonly ConcurrentQueue<IChannel> _idleChannels = new();

    // Every channel currently rented out (returned by RentChannelAsync, not yet passed to
    // ReleaseChannelAsync) - without this, DisposeAsync can only see _idleChannels, so a channel
    // held by an in-flight PublishAsync/TryRepublishAsync call at shutdown is never disposed. Access
    // to this set is always paired with a _disposeLock check (see DisposeAsync/ReleaseChannelAsync)
    // so "who disposes this channel" is decided exactly once even if a release races the shutdown.
    private readonly HashSet<IChannel> _outstandingChannels = [];
    private readonly Lock _disposeLock = new();
    private bool _disposed;

    public async Task PublishAsync<TMessage>(TMessage message, string topic, CancellationToken cancellationToken, ActivityContext? parentContext = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        ArgumentNullException.ThrowIfNull(message);

        var messageId = Guid.NewGuid().ToString();
        var payload = JsonSerializer.Serialize(message);

        var published = await TryPublishAsync(messageId, topic, payload, cancellationToken, parentContext);
        if (!published)
        {
            await _fallbackStore.SaveAsync(
                new FallbackMessage(messageId, topic, payload, DateTime.UtcNow),
                cancellationToken);
        }
    }

    public Task<bool> TryRepublishAsync(FallbackMessage message, CancellationToken cancellationToken) =>
        TryPublishAsync(message.MessageId, message.Topic, message.Payload, cancellationToken, parentContext: null);

    private async Task<bool> TryPublishAsync(
        string messageId,
        string topic,
        string payload,
        CancellationToken cancellationToken,
        ActivityContext? parentContext)
    {
        // An explicit parentContext (see IMessagePublisher.PublishAsync) wins over whatever
        // Activity.Current happens to be ambient right now - set when this runs on
        // LocalPublishQueueWorker's loop, where Current is no longer the original caller's.
        using var activity = parentContext is { } context
            ? MessagingActivitySource.Instance.StartActivity($"{topic} publish", ActivityKind.Producer, context)
            : MessagingActivitySource.Instance.StartActivity($"{topic} publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", MessagingConstants.EventsExchange);
        activity?.SetTag("messaging.rabbitmq.routing_key", topic);
        activity?.SetTag("messaging.message.id", messageId);

        IChannel? channel = null;
        var healthy = false;
        try
        {
            channel = await RentChannelAsync(cancellationToken).ConfigureAwait(false);

            var properties = new BasicProperties
            {
                MessageId = messageId,
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent
            };

            // Carries the trace context across the async boundary so the consumer's span
            // links back to this one (see RabbitMqConsumer.TryExtractParentContext).
            if (activity?.Id is { } traceParent)
            {
                properties.Headers = new Dictionary<string, object?> { ["traceparent"] = traceParent };
            }

            await channel.BasicPublishAsync(
                exchange: MessagingConstants.EventsExchange,
                routingKey: topic,
                mandatory: false,
                basicProperties: properties,
                body: Encoding.UTF8.GetBytes(payload),
                cancellationToken: cancellationToken);

            healthy = true;
            // Debug, not Information - one publish per CreateLink/redirect request, same volume
            // concern as the controllers that call this. Shows the publish leg of the flow when a
            // service is flipped to Debug locally.
            _logger.LogDebug("Published message {MessageId} to topic {Topic}", messageId, topic);
            return true;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            // The only other trace of this is the OTel span above, which nothing but a trace backend
            // sees - without a plain log line, "RabbitMQ is unreachable" looks identical in the logs
            // to "nobody is posting links", since the caller falls back to SQLite and moves on.
            _logger.LogWarning(ex, "Failed to publish message {MessageId} to topic {Topic} - falling back to local storage", messageId, topic);
            return false;
        }
        finally
        {
            if (channel is not null)
            {
                await ReleaseChannelAsync(channel, healthy).ConfigureAwait(false);
            }
        }
    }

    // Reuses an idle channel if one's sitting in the pool; otherwise opens (and exchange-declares)
    // a fresh one, up to PoolCapacity concurrently outstanding. Once at capacity, callers block on
    // the semaphore until a channel comes back - deliberate backpressure instead of unbounded
    // channel creation under a burst.
    private async Task<IChannel> RentChannelAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Everything from here on holds a slot - any exception (including a stale idle channel
        // failing to dispose, or the exchange declare below) must release it before propagating, or
        // the pool permanently loses capacity and every publish eventually blocks forever.
        try
        {
            while (_idleChannels.TryDequeue(out var idle))
            {
                if (idle.IsOpen)
                {
                    TrackOutstanding(idle);
                    return idle;
                }

                await idle.DisposeAsync().ConfigureAwait(false);
            }

            IChannel? channel = null;
            try
            {
                channel = await _connection.CreateChannelAsync(cancellationToken).ConfigureAwait(false);
                await channel.ExchangeDeclareAsync(
                    MessagingConstants.EventsExchange,
                    ExchangeType.Topic,
                    durable: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                TrackOutstanding(channel);
                return channel;
            }
            catch
            {
                // The channel opened fine but the declare failed (or was cancelled) - without this,
                // the open channel is never disposed and leaks on the broker until channel_max is
                // exhausted.
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

    private void TrackOutstanding(IChannel channel)
    {
        lock (_disposeLock)
        {
            _outstandingChannels.Add(channel);
        }
    }

    // A channel that just faulted (broker hiccup, publish exception) is discarded rather than
    // pooled - RentChannelAsync creates a replacement lazily on the next call, same as the pool
    // starting empty.
    private async Task ReleaseChannelAsync(IChannel channel, bool healthy)
    {
        try
        {
            // claimedByDispose: true means DisposeAsync's own lock already ran first and removed
            // this channel from _outstandingChannels into ITS disposal list (see below) - in that
            // case this call must not touch the channel at all, or both this call and DisposeAsync's
            // loop would call DisposeAsync() on the same channel. shouldDispose/shouldEnqueue are
            // decided in the same lock as that removal so the three outcomes ("I own it, keep it
            // idle" / "I own it, dispose it" / "DisposeAsync already owns it") are mutually
            // exclusive no matter how this races against a concurrent DisposeAsync call.
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
            }

            if (shouldDispose)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            // Must run even if DisposeAsync above throws - otherwise the slot is lost permanently,
            // and the exception would also escape TryPublishAsync's own finally, skipping the
            // SQLite-fallback path entirely instead of just failing this one publish.
            _slots.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Snapshotting _outstandingChannels and setting _disposed under the same lock a racing
        // ReleaseChannelAsync uses is what closes the actual bug this fixes: a publish still
        // in-flight at shutdown holds a channel that isn't in _idleChannels yet, so draining only
        // that queue (the old behavior) misses it - if its ReleaseChannelAsync call happens to run
        // after this method already returned, the channel would be enqueued into a queue nothing
        // ever drains again and leaked for the rest of the process's life. Every channel this method
        // doesn't find in _outstandingChannels was already handed off to (and will be disposed by) a
        // concurrent ReleaseChannelAsync instead - see the "stillOutstanding" check there.
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
}
