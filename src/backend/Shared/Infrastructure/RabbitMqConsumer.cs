using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Infrastructure;

public sealed class RabbitMqConsumer(
    IRabbitMqConnection connection,
    ILogger<RabbitMqConsumer> logger,
    IConfiguration configuration) : IMessageConsumer
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    // A permanently-failing message (bad JSON, a handler that always throws for this payload) must
    // not be nacked-with-requeue forever: RabbitMQ redelivers it immediately, so that's a hot loop
    // pinning the consumer's CPU and re-running the same failing DB round trip at full speed with the
    // queue never draining. After this many attempts the message is dropped (nacked without requeue)
    // instead - there's no dead-letter exchange configured on these queues, so "dropped" really does
    // mean gone, which is an accepted tradeoff for this learning project over building out a DLQ and
    // something to drain it.
    private const int MaxDeliveryAttempts = 5;
    private static readonly TimeSpan InitialHandlerRetryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxHandlerRetryDelay = TimeSpan.FromSeconds(2);

    private readonly IRabbitMqConnection _connection = connection;
    private readonly ILogger<RabbitMqConsumer> _logger = logger;

    // Keyed by the publisher-assigned MessageId (stable across redeliveries of the same logical
    // message, unlike DeliveryTag which changes every redelivery). Entries are removed as soon as a
    // message is acked or given up on, so this only ever holds messages currently mid-retry - it
    // resets on process restart, which just means a message gets a fresh set of attempts after a
    // restart rather than picking up its old count; acceptable here since the point is bounding a
    // single consumer's hot loop, not a durable retry ledger.
    private readonly ConcurrentDictionary<string, int> _deliveryAttempts = new();

    // How many unacked deliveries this consumer's channel can hold at once - the classic
    // competing-consumers throughput/fairness knob. Config-bound (not hardcoded) specifically so
    // control-api's experimental prefetch control can change it via a container recreate - see
    // RabbitMq__PrefetchCount in docker-compose.yml.
    private readonly ushort _prefetchCount = configuration.GetValue<ushort?>("RabbitMq:PrefetchCount") ?? 10;

    public async Task ConsumeAsync<TMessage>(
        string queueName,
        string routingKey,
        Func<TMessage, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);
        ArgumentException.ThrowIfNullOrEmpty(routingKey);
        ArgumentNullException.ThrowIfNull(handler);

        // The broker being briefly unreachable at startup (a container health check that passed
        // before the AMQP listener actually opened, a restart, ...) shouldn't crash this worker —
        // that's exactly the kind of transient condition retrying should absorb.
        var channel = await SetUpChannelWithRetryAsync(queueName, routingKey, cancellationToken).ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_1, ea) => HandleDeliveryAsync(channel, queueName, routingKey, ea, handler, cancellationToken);

            await channel.BasicConsumeAsync(queueName, autoAck: false, consumer, cancellationToken);

            // Keep the channel (and this call) alive until the caller cancels.
            var stopped = new TaskCompletionSource();
            await using var registration = cancellationToken.Register(() => stopped.TrySetResult());
            await stopped.Task.ConfigureAwait(false);
        }
    }

    internal async Task HandleDeliveryAsync<TMessage>(
        IChannel channel,
        string queueName,
        string routingKey,
        BasicDeliverEventArgs ea,
        Func<TMessage, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        using var activity = StartConsumerActivity(routingKey, ea);
        var messageId = ea.BasicProperties.MessageId ?? ea.DeliveryTag.ToString();
        try
        {
            var json = Encoding.UTF8.GetString(ea.Body.Span);
            var message = JsonSerializer.Deserialize<TMessage>(json);
            if (message is not null)
            {
                await handler(message, cancellationToken);
            }

            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken);
            _deliveryAttempts.TryRemove(messageId, out _);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            var attempts = _deliveryAttempts.AddOrUpdate(messageId, 1, (_, count) => count + 1);
            if (attempts >= MaxDeliveryAttempts)
            {
                _deliveryAttempts.TryRemove(messageId, out _);
                _logger.LogError(
                    ex,
                    "Handler failed for message {MessageId} on queue {QueueName} after {Attempts} attempts - dropping it instead of requeuing forever",
                    messageId,
                    queueName,
                    attempts);
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken);
                return;
            }

            _logger.LogWarning(
                ex,
                "Handler failed for message {MessageId} on queue {QueueName} (attempt {Attempt}/{MaxAttempts}), nacking for requeue",
                messageId,
                queueName,
                attempts,
                MaxDeliveryAttempts);

            // A brief, attempt-scaled delay before requeueing so a permanently-failing message spins
            // at a few retries per second instead of pinning the CPU with immediate redelivery - short
            // enough not to meaningfully slow down recovery from a genuinely transient failure (a
            // momentary DB blip resolves well within MaxHandlerRetryDelay).
            var delay = TimeSpan.FromMilliseconds(
                Math.Min(InitialHandlerRetryDelay.TotalMilliseconds * attempts, MaxHandlerRetryDelay.TotalMilliseconds));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, cancellationToken);
        }
    }

    internal async Task<IChannel> SetUpChannelWithRetryAsync(
        string queueName,
        string routingKey,
        CancellationToken cancellationToken)
    {
        var delay = InitialRetryDelay;
        while (true)
        {
            IChannel? channel = null;
            try
            {
                channel = await _connection.CreateChannelAsync(cancellationToken).ConfigureAwait(false);

                await channel.ExchangeDeclareAsync(
                    MessagingConstants.EventsExchange,
                    ExchangeType.Topic,
                    durable: true,
                    cancellationToken: cancellationToken);

                await channel.QueueDeclareAsync(
                    queueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    cancellationToken: cancellationToken);

                await channel.QueueBindAsync(
                    queueName,
                    MessagingConstants.EventsExchange,
                    routingKey,
                    cancellationToken: cancellationToken);

                await channel.BasicQosAsync(0, prefetchCount: _prefetchCount, global: false, cancellationToken: cancellationToken);

                return channel;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The channel may have opened fine even though a later declare/bind/QoS call
                // failed - without disposing it here, every retry round (up to every 30s during a
                // sustained outage) leaks another open channel on the broker.
                if (channel is not null)
                {
                    await channel.DisposeAsync().ConfigureAwait(false);
                }

                _logger.LogWarning(
                    ex,
                    "Failed to set up RabbitMQ consumer for queue {QueueName}, retrying in {Delay}",
                    queueName,
                    delay);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxRetryDelay.TotalSeconds));
            }
        }
    }

    private static Activity? StartConsumerActivity(string routingKey, BasicDeliverEventArgs ea)
    {
        var parentContext = TryExtractParentContext(ea.BasicProperties);
        var activity = parentContext is { } context
            ? MessagingActivitySource.Instance.StartActivity($"{routingKey} consume", ActivityKind.Consumer, context)
            : MessagingActivitySource.Instance.StartActivity($"{routingKey} consume", ActivityKind.Consumer);

        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", MessagingConstants.EventsExchange);
        activity?.SetTag("messaging.rabbitmq.routing_key", routingKey);
        activity?.SetTag("messaging.message.id", ea.BasicProperties.MessageId);
        return activity;
    }

    // The publisher sends the trace context as a "traceparent" header (W3C format) so this span
    // links back to the one that published the message, instead of starting an unrelated trace.
    private static ActivityContext? TryExtractParentContext(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers?.TryGetValue("traceparent", out var raw) == true &&
            raw is byte[] bytes &&
            ActivityContext.TryParse(Encoding.UTF8.GetString(bytes), null, out var context))
        {
            return context;
        }

        return null;
    }
}
