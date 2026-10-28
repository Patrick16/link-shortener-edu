namespace ControlApi.Models;

// A non-destructive peek at a dead-lettered message - read via the RabbitMQ Management API's
// "get" endpoint with ackmode=ack_requeue_true, so sampling never removes anything (safe to call
// repeatedly before deciding whether to replay). Reason is whatever RabbitMqConsumer's own
// PublishDeadLetterAsync set on the "x-dead-reason" header (e.g. "deserialize-failed" or a batch
// handler's own rejection reason) - null if that header is somehow missing.
public sealed record DeadLetterMessageSample(string? MessageId, string? Reason, string Payload);

public sealed record DlqReplayResult(string QueueName, int Replayed);
