using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace Infrastructure;

public sealed class RabbitMqPublisher(
    IRabbitMqConnection connection,
    IMessageFallbackStore fallbackStore) : IMessagePublisher, IAsyncDisposable
{
    private readonly IRabbitMqConnection _connection = connection;
    private readonly IMessageFallbackStore _fallbackStore = fallbackStore;

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

    public async Task PublishAsync<TMessage>(TMessage message, string topic, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        ArgumentNullException.ThrowIfNull(message);

        var messageId = Guid.NewGuid().ToString();
        var payload = JsonSerializer.Serialize(message);

        var published = await TryPublishAsync(messageId, topic, payload, cancellationToken);
        if (!published)
        {
            await _fallbackStore.SaveAsync(
                new FallbackMessage(messageId, topic, payload, DateTime.UtcNow),
                cancellationToken);
        }
    }

    public Task<bool> TryRepublishAsync(FallbackMessage message, CancellationToken cancellationToken) =>
        TryPublishAsync(message.MessageId, message.Topic, message.Payload, cancellationToken);

    private async Task<bool> TryPublishAsync(string messageId, string topic, string payload, CancellationToken cancellationToken)
    {
        using var activity = MessagingActivitySource.Instance.StartActivity($"{topic} publish", ActivityKind.Producer);
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
            return true;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
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
                MessagingConstants.EventsExchange,
                ExchangeType.Topic,
                durable: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return channel;
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    // A channel that just faulted (broker hiccup, publish exception) is discarded rather than
    // pooled - RentChannelAsync creates a replacement lazily on the next call, same as the pool
    // starting empty.
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

    public async ValueTask DisposeAsync()
    {
        while (_idleChannels.TryDequeue(out var channel))
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
