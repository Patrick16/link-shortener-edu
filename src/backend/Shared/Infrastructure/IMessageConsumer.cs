namespace Infrastructure;

public interface IMessageConsumer
{
    // Subscribes to `routingKey` on the shared events exchange via a durable queue named `queueName`,
    // and awaits until `cancellationToken` fires. Deliveries are buffered and handed to `handler` in
    // batches (see RabbitMqConsumer's RabbitMq:BatchSize/RabbitMq:BatchTimeoutMs config) instead of
    // one at a time, so the handler can do one bulk write per batch instead of one round trip per
    // message. A handler that returns names any permanently-bad messages (by MessageId) in the
    // returned BatchOutcome - those are dead-lettered to "{queueName}.dead", everything else in the
    // batch is acked. A handler that throws is treated as a batch-wide transient failure: the whole
    // batch is nacked-and-requeued with backoff, and a message is only dead-lettered once its own
    // redelivery count exceeds RabbitMqConsumer's retry budget.
    Task ConsumeBatchAsync<TMessage>(
        string queueName,
        string routingKey,
        Func<IReadOnlyList<BatchItem<TMessage>>, CancellationToken, Task<BatchOutcome>> handler,
        CancellationToken cancellationToken);
}
