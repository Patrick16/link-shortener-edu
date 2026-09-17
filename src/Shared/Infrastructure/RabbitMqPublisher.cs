using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace Infrastructure;

public sealed class RabbitMqPublisher(
    IRabbitMqConnection connection,
    IMessageFallbackStore fallbackStore) : IMessagePublisher
{
    private const string ExchangeName = "events";

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
        try
        {
            var channel = await _connection.CreateChannelAsync(cancellationToken);
            await using (channel.ConfigureAwait(false))
            {
                await channel.ExchangeDeclareAsync(
                    ExchangeName,
                    ExchangeType.Topic,
                    durable: true,
                    cancellationToken: cancellationToken);

                var properties = new BasicProperties
                {
                    MessageId = messageId,
                    ContentType = "application/json",
                    DeliveryMode = DeliveryModes.Persistent
                };

                await channel.BasicPublishAsync(
                    exchange: ExchangeName,
                    routingKey: topic,
                    mandatory: false,
                    basicProperties: properties,
                    body: Encoding.UTF8.GetBytes(payload),
                    cancellationToken: cancellationToken);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
