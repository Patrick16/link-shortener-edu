using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Infrastructure;

public sealed class RabbitMqConsumer(
    IRabbitMqConnection connection,
    ILogger<RabbitMqConsumer> logger) : IMessageConsumer
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly IRabbitMqConnection _connection = connection;
    private readonly ILogger<RabbitMqConsumer> _logger = logger;

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
            consumer.ReceivedAsync += async (_, ea) =>
            {
                using var activity = StartConsumerActivity(routingKey, ea);
                try
                {
                    var json = Encoding.UTF8.GetString(ea.Body.Span);
                    var message = JsonSerializer.Deserialize<TMessage>(json);
                    if (message is not null)
                    {
                        await handler(message, cancellationToken);
                    }

                    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken);
                }
                catch (Exception ex)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    _logger.LogError(ex, "Handler failed for message on queue {QueueName}, nacking for requeue", queueName);
                    await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, cancellationToken);
                }
            };

            await channel.BasicConsumeAsync(queueName, autoAck: false, consumer, cancellationToken);

            // Keep the channel (and this call) alive until the caller cancels.
            var stopped = new TaskCompletionSource();
            await using var registration = cancellationToken.Register(() => stopped.TrySetResult());
            await stopped.Task.ConfigureAwait(false);
        }
    }

    private async Task<IChannel> SetUpChannelWithRetryAsync(
        string queueName,
        string routingKey,
        CancellationToken cancellationToken)
    {
        var delay = InitialRetryDelay;
        while (true)
        {
            try
            {
                var channel = await _connection.CreateChannelAsync(cancellationToken).ConfigureAwait(false);

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

                await channel.BasicQosAsync(0, prefetchCount: 10, global: false, cancellationToken: cancellationToken);

                return channel;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
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
