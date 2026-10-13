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
    // null if the rabbitmq container isn't running. Teaching-only action: nothing in this project
    // re-publishes a dead-lettered message, so there's no "undo" to offer and no real poison
    // messages are expected to accumulate here in practice - this just clears the demo noise.
    Task<int?> ClearDeadLetterQueuesAsync(CancellationToken ct);
}
