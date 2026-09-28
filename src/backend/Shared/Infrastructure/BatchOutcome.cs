namespace Infrastructure;

// Returned by a batch handler to report which messages (by MessageId) are permanently
// unprocessable - malformed in a way retrying will never fix - so RabbitMqConsumer can dead-letter
// just those and ack the rest of the batch. A handler that instead throws is reporting a batch-wide,
// presumably transient failure (e.g. the database is unreachable): RabbitMqConsumer nacks-and-
// requeues the whole batch for that case instead - see RabbitMqConsumer.HandleBatchFailureAsync.
public sealed class BatchOutcome
{
    public static readonly BatchOutcome Success = new(Array.Empty<string>());

    private BatchOutcome(IReadOnlyCollection<string> poisonMessageIds) => PoisonMessageIds = poisonMessageIds;

    public IReadOnlyCollection<string> PoisonMessageIds { get; }

    public static BatchOutcome WithPoison(IReadOnlyCollection<string> poisonMessageIds) => new(poisonMessageIds);
}
