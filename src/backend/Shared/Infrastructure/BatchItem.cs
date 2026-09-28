namespace Infrastructure;

// One already-deserialized message handed to a batch handler. Deliberately doesn't carry the
// underlying RabbitMQ delivery tag - ack/nack/dead-letter decisions for the whole batch are made by
// RabbitMqConsumer itself, based on whether the handler throws and which MessageIds it names as
// poison in the BatchOutcome it returns.
public sealed record BatchItem<TMessage>(string MessageId, TMessage Payload);
