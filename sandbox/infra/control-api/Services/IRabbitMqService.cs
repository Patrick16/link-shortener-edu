using ControlApi.Models;

namespace ControlApi.Services;

public interface IRabbitMqService
{
    // Live depth of every "{queue}.dead" queue, read directly off RabbitMQ via `rabbitmqctl
    // list_queues` in a Docker exec - not polled/cached, a fresh snapshot on every call. Null means
    // the rabbitmq container isn't running.
    Task<DeadLetterQueueStats?> GetDeadLetterQueueStatsAsync(CancellationToken ct);
}
