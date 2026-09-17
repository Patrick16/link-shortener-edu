namespace Infrastructure;

public interface IMessageConsumer
{
    // Subscribes to `routingKey` on the shared events exchange via a durable queue named `queueName`,
    // and awaits until `cancellationToken` fires. Each message is deserialized to TMessage and handed
    // to `handler`; an exception from `handler` nacks-and-requeues the message, a normal return acks it.
    Task ConsumeAsync<TMessage>(
        string queueName,
        string routingKey,
        Func<TMessage, CancellationToken, Task> handler,
        CancellationToken cancellationToken);
}
