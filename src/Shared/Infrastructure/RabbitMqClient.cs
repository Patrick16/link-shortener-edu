using RabbitMQ.Client;

namespace Infrastructure;

public interface IRabbitMqConnection
{
    Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default);
}

// Connection wrapper around RabbitMQ, used by all services.
//
// The connection is established lazily and on-demand, not in the constructor: this type is
// typically registered as a DI singleton and constructed at host startup, when the broker may not
// be reachable yet (a transient blip, or — in docker-compose — a "healthy" dependency container
// whose AMQP listener isn't actually bound yet). A failed attempt is not cached: the next caller
// gets a fresh connection attempt instead of forever re-throwing the first failure.
public sealed class RabbitMqClient(string connectionString) : IRabbitMqConnection, IAsyncDisposable
{
    private readonly ConnectionFactory _factory = new() { Uri = new Uri(connectionString) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<IConnection>? _connection;

    public async Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        var existing = _connection;
        if (IsUsable(existing))
        {
            return await existing!.ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = _connection;
            if (IsUsable(existing))
            {
                return await existing!.ConfigureAwait(false);
            }

            var connectTask = _factory.CreateConnectionAsync(cancellationToken);
            _connection = connectTask;
            return await connectTask.ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsUsable(Task<IConnection>? connection) =>
        connection is not null && connection.Status is not (TaskStatus.Faulted or TaskStatus.Canceled);

    public async ValueTask DisposeAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        try
        {
            var established = await connection.ConfigureAwait(false);
            await established.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Never connected successfully (or was already broken) — nothing to dispose.
        }
    }
}
