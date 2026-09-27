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

            if (existing is { Status: TaskStatus.RanToCompletion })
            {
                // IsUsable already found this one dead (IsOpen is false) - dispose it before
                // replacing it so the closed connection's resources aren't held onto forever. A
                // broker-side close may have already torn it down, so a throw here is expected, not
                // exceptional - it must not stop the reconnect below.
                try
                {
                    await existing.Result.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
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

    internal static bool IsUsable(Task<IConnection>? connection)
    {
        if (connection is null || connection.Status is TaskStatus.Faulted or TaskStatus.Canceled)
        {
            return false;
        }

        // Still connecting - callers will just await it; nothing to check yet.
        if (connection.Status != TaskStatus.RanToCompletion)
        {
            return true;
        }

        // Once a connection attempt succeeds, this task sits at RanToCompletion forever - even after
        // RabbitMQ restarts, a chaos experiment kills the connection, or a network blip drops it.
        // Without checking IsOpen here, every future caller (and the retry loops in
        // RabbitMqConsumer/RabbitMqPublisher) would keep getting handed the same dead IConnection:
        // CreateChannelAsync on it throws, the caller logs, waits, and retries - but retrying just
        // calls back in here and finds the same "usable" (RanToCompletion) task again, so the retry
        // can never actually succeed even once the broker comes back up.
        return connection.Result.IsOpen;
    }

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
