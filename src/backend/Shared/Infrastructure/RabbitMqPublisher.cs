using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace Infrastructure;

public sealed class RabbitMqPublisher(
    IRabbitMqConnection connection,
    IMessageFallbackStore fallbackStore) : IMessagePublisher
{
    private readonly IRabbitMqConnection _connection = connection;
    private readonly IMessageFallbackStore _fallbackStore = fallbackStore;

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

        try
        {
            var channel = await _connection.CreateChannelAsync(cancellationToken);
            await using (channel.ConfigureAwait(false))
            {
                await channel.ExchangeDeclareAsync(
                    MessagingConstants.EventsExchange,
                    ExchangeType.Topic,
                    durable: true,
                    cancellationToken: cancellationToken);

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
            }

            return true;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return false;
        }
    }
}
