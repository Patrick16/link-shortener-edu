using ControlApi.Models;

namespace ControlApi.Services;

public interface IRabbitMqService
{
    // Live depth of every "{queue}.dead" queue, read directly off RabbitMQ via `rabbitmqctl
    // list_queues` in a Docker exec - not polled/cached, a fresh snapshot on every call. Null means
    // the rabbitmq container isn't running.
    Task<DeadLetterQueueStats?> GetDeadLetterQueueStatsAsync(CancellationToken ct);

    // Purges every "{queue}.dead" queue via `rabbitmqctl purge_queue`, one exec per queue - there's
    // no native rabbitmqctl command to purge by name pattern, only by exact queue name, so each dead
    // queue found in a fresh listing gets its own call. Returns the total message count purged
    // (snapshotted right before purging, since purge_queue's own output doesn't report a count), or
    // null if the rabbitmq container isn't running. Teaching-only action: this just clears the demo
    // noise when a replay isn't warranted (or already happened).
    Task<int?> ClearDeadLetterQueuesAsync(CancellationToken ct);

    // Non-destructive peek at up to `count` messages currently sitting in `deadQueueName` (e.g.
    // "shortener-service.link-created.dead", the exact name GetDeadLetterQueueStatsAsync already
    // reports) - read via the RabbitMQ Management HTTP API's "get" endpoint with
    // ackmode=ack_requeue_true, so nothing is removed. Null if the rabbitmq container isn't
    // running or the queue doesn't exist.
    Task<IReadOnlyList<DeadLetterMessageSample>?> SampleDeadLetterQueueAsync(string deadQueueName, int count, CancellationToken ct);

    // Moves up to `count` messages from `deadQueueName` back onto the live queue it originally
    // dead-lettered from (the same name with the ".dead" suffix stripped) - see RabbitMqService's
    // own comment on why this publishes each message to the live queue FIRST and only removes it
    // from the dead queue after that succeeds, not the other way around. Safe only for consumers
    // that are genuinely redelivery-safe (checked for every current dead-letter producer before
    // this was built - see PLAN.md's own item 13). Null if the rabbitmq container isn't running.
    Task<DlqReplayResult?> ReplayDeadLetterQueueAsync(string deadQueueName, int count, CancellationToken ct);
}
