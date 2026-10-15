namespace Infrastructure;

// One permanently-unprocessable message and why - the reason travels all the way to the dead
// queue's "x-dead-reason" header (see RabbitMqConsumer.PublishDeadLetterAsync), so an operator
// looking at a dead-lettered message doesn't have to go spelunking in logs to know what happened.
public sealed record PoisonMessage(string MessageId, string Reason);

// Returned by a batch handler to report which messages are permanently unprocessable - malformed
// or in conflict with already-persisted data in a way retrying will never fix - so RabbitMqConsumer
// can dead-letter just those and ack the rest of the batch. A handler that instead throws is
// reporting a batch-wide, presumably transient failure (e.g. the database is unreachable):
// RabbitMqConsumer nacks-and-requeues the whole batch for that case instead - see
// RabbitMqConsumer.HandleBatchFailureAsync.
public sealed class BatchOutcome
{
    public static readonly BatchOutcome Success = new(Array.Empty<PoisonMessage>());

    private BatchOutcome(IReadOnlyCollection<PoisonMessage> poisonMessages) => PoisonMessages = poisonMessages;

    public IReadOnlyCollection<PoisonMessage> PoisonMessages { get; }

    public static BatchOutcome WithPoison(IReadOnlyCollection<PoisonMessage> poisonMessages) => new(poisonMessages);
}
