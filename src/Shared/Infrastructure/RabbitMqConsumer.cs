using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Infrastructure;

public sealed class RabbitMqConsumer(IRabbitMqConnection connection) : IMessageConsumer
{
    private readonly IRabbitMqConnection _connection = connection;

    public async Task ConsumeAsync<TMessage>(
        string queueName,
        string routingKey,
        Func<TMessage, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);
        ArgumentException.ThrowIfNullOrEmpty(routingKey);
        ArgumentNullException.ThrowIfNull(handler);

        var channel = await _connection.CreateChannelAsync(cancellationToken);
        await using (channel.ConfigureAwait(false))
        {
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

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
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
                catch (Exception)
                {
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
}
