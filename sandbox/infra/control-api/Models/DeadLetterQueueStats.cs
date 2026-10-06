namespace ControlApi.Models;

// One row per "{queue}.dead" queue RabbitMQ currently knows about - see RabbitMqConsumer's own
// PublishDeadLetterAsync for how a message lands there (MaxDeliveryAttempts exceeded, a deserialize
// failure, or a handler-flagged business rejection - the first goes through 5 retries first, the
// other two dead-letter immediately). Nothing consumes these queues today, so MessageCount only
// grows from this app's own code paths - an operator purging/acking a message directly via the
// RabbitMQ management UI can still lower it. This is a "notice something landed here" signal, not a
// live operational metric like pgcat/postgres connection stats.
public record DeadLetterQueueDepth(string QueueName, int MessageCount);

public record DeadLetterQueueStats(IReadOnlyList<DeadLetterQueueDepth> Queues, int TotalMessages);
