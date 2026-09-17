using RabbitMQ.Client;

namespace Infrastructure;

public interface IRabbitMqConnection
{
    Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default);
}

// Connection wrapper around RabbitMQ, used by all services.
public sealed class RabbitMqClient : IRabbitMqConnection, IAsyncDisposable
{
    private readonly Task<IConnection> _connection;

    public RabbitMqClient(string connectionString)
    {
        var factory = new ConnectionFactory { Uri = new Uri(connectionString) };
        _connection = factory.CreateConnectionAsync();
    }

    public async Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _connection.ConfigureAwait(false);
        return await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        var connection = await _connection.ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }
}
