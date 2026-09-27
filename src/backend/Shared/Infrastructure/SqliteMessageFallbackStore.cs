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
    // If link-api/redirect-api are scaled to N replicas (a first-class, supported feature - see the
    // "No host port mapping - multiple replicas" comment on link-api in docker-compose.yml), every
    // replica's RabbitMqRetryWorker points at the same shared volume/file (see
    // ConnectionStrings__RabbitMqFallback). A claim that's stuck this long (its replica crashed
    // mid-tick, or a republish call itself hung) is released so another replica's tick can pick the
    // row back up - short enough that a real outage still recovers promptly, long enough to never
    // fire while a normal in-flight tick (PollInterval below is 30s) is still working the row.
    private static readonly TimeSpan StaleClaimThreshold = TimeSpan.FromMinutes(2);
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
                CreatedAt TEXT NOT NULL,
                ClaimedAt TEXT NULL
            );
            """;
        command.ExecuteNonQuery();

        // A file created by an older version of this store won't have ClaimedAt - CREATE TABLE IF
        // NOT EXISTS above is a no-op against it. SQLite has no "ADD COLUMN IF NOT EXISTS", so check
        // first; adding it unconditionally would throw "duplicate column name" on every subsequent
        // start once the column does exist.
        using var checkColumn = connection.CreateCommand();
        checkColumn.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{TableName}') WHERE name = 'ClaimedAt';";
        var hasClaimedAtColumn = (long)checkColumn.ExecuteScalar()! > 0;
        if (!hasClaimedAtColumn)
        {
            using var addColumn = connection.CreateCommand();
            addColumn.CommandText = $"ALTER TABLE {TableName} ADD COLUMN ClaimedAt TEXT NULL;";
            addColumn.ExecuteNonQuery();
        }
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

        // A plain SELECT here let two replicas (see StaleClaimThreshold's comment on why this
        // matters) both pick up the same row before either deleted it, both republish it, and the
        // event gets processed twice downstream - RabbitMqConsumer has no message-id dedup, only a
        // tracing tag. Marking ClaimedAt as part of the same statement that selects the rows makes
        // "which rows this call gets" and "marking them as spoken for" one atomic operation - SQLite
        // serializes writers, so a second connection's UPDATE can only see whatever the first
        // connection's UPDATE didn't already claim, by the time it runs.
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {TableName}
            SET ClaimedAt = $now
            WHERE MessageId IN (
                SELECT MessageId FROM {TableName}
                WHERE ClaimedAt IS NULL OR ClaimedAt <= $staleThreshold
                ORDER BY CreatedAt
                LIMIT {MaxPendingPerFetch}
            )
            RETURNING MessageId, Topic, Payload, CreatedAt;
            """;
        var now = DateTime.UtcNow;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$staleThreshold", (now - StaleClaimThreshold).ToString("O"));

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

        // RETURNING reflects the order rows were physically updated in, not the subquery's own
        // ORDER BY (which only picked which rows to include, via LIMIT) - re-sorting here is what
        // actually delivers on GetPendingAsync's documented oldest-first contract.
        results.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
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
