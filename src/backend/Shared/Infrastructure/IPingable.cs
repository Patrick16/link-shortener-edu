namespace Infrastructure;

// Shared by any dependency a health check needs to probe but that isn't itself a DbContext/Mongo/
// RabbitMQ client with its own dedicated health check already - e.g. IClickFactStore and
// IClickFactQueryService both implement this instead of each separately declaring an identical
// PingAsync member, which is what let ClickHouseHealthCheck<T> stay a plain generic class
// constructor-injected with its dependency, the same shape as DbContextHealthCheck<TContext>,
// instead of a delegate-based workaround.
public interface IPingable
{
    Task<bool> PingAsync(CancellationToken cancellationToken = default);
}
