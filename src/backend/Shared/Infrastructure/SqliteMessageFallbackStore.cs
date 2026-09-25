using Microsoft.Data.Sqlite;

namespace Infrastructure;

// Fallback storage for outgoing bus messages when RabbitMQ is unavailable.
public sealed class SqliteMessageFallbackStore : IMessageFallbackStore
{
    private const string TableName = "FailedMessages";
    // Caps how much a single retry-worker tick can load/replay - during a long broker outage under
    // load, the table can grow into the tens or hundreds of thousands of rows; loading all of them
    // every 30s would spike memory and turn one tick into an hours-long serial retry pass. Whatever
    // doesn't fit this batch is simply picked up (oldest-first) on a later tick once the backlog
    // shrinks.
    private const int MaxPendingPerFetch = 500;
    private readonly string _connectionString;

    public SqliteMessageFallbackStore(string connectionString)
    {
        _connectionString = connectionString;
        EnsureTableCreated();
    }

    private void EnsureTableCreated()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {TableName} (
                MessageId TEXT PRIMARY KEY,
                Topic TEXT NOT NULL,
                Payload TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public async Task SaveAsync(FallbackMessage message, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {TableName} (MessageId, Topic, Payload, CreatedAt)
            VALUES ($messageId, $topic, $payload, $createdAt);
            """;
        command.Parameters.AddWithValue("$messageId", message.MessageId);
        command.Parameters.AddWithValue("$topic", message.Topic);
        command.Parameters.AddWithValue("$payload", message.Payload);
        command.Parameters.AddWithValue("$createdAt", message.CreatedAt.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FallbackMessage>> GetPendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT MessageId, Topic, Payload, CreatedAt FROM {TableName} ORDER BY CreatedAt LIMIT {MaxPendingPerFetch};";

        var results = new List<FallbackMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new FallbackMessage(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return results;
    }

    public async Task DeleteAsync(string messageId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {TableName} WHERE MessageId = $messageId;";
        command.Parameters.AddWithValue("$messageId", messageId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
