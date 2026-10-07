using Common.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Infrastructure;

// Owns its own MongoClient/IMongoDatabase, same lifecycle pattern as RabbitMqClient — constructed
// once from a connection string (database name comes from the URL, e.g.
// "mongodb://mongo:27017/clicks_meta_db") and registered as a DI singleton; MongoClient itself
// already pools and reconnects internally, so no extra retry wrapper is needed here.
public sealed class MongoClickMetaStore : IClickMetaStore
{
    private const string CollectionName = "clicks";

    // Retention window for click metadata - kept in step with ClickHouse's own TTL on reports_db
    // .clicks (sandbox/infra/clickhouse/init.sql) so the story is consistent across every store
    // this project writes click data to, not a value with any other significance. Mongo's TTL
    // monitor runs roughly once a minute and deletes expired documents on its own - no job to build
    // or schedule, unlike Postgres's partition-drop (see .notes/PLAN.md's Scenario 4 Infra section)
    // or ClickHouse's merge-time TTL enforcement.
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(90);

    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<ClickMeta> _collection;

    public MongoClickMetaStore(string connectionString)
    {
        var url = MongoUrl.Create(connectionString);
        _database = new MongoClient(url).GetDatabase(url.DatabaseName);
        _collection = _database.GetCollection<ClickMeta>(CollectionName);
    }

    public Task SaveManyAsync(IReadOnlyCollection<ClickMeta> metas, CancellationToken cancellationToken = default)
    {
        if (metas.Count == 0)
        {
            return Task.CompletedTask;
        }

        // One bulk round trip instead of one upsert per click. Each entry is still its own
        // independent upsert by Id - redelivery-safe on its own, independent of the "already stored
        // in Postgres" check in ClickTrackedConsumer (defense in depth — the two writes aren't
        // transactional with each other).
        var writes = metas.Select(meta =>
            new ReplaceOneModel<ClickMeta>(Builders<ClickMeta>.Filter.Eq(x => x.Id, meta.Id), meta) { IsUpsert = true });

        return _collection.BulkWriteAsync(writes, cancellationToken: cancellationToken);
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        var model = new CreateIndexModel<ClickMeta>(
            Builders<ClickMeta>.IndexKeys.Ascending(x => x.ClickedAt),
            new CreateIndexOptions { ExpireAfter = RetentionWindow });
        return _collection.Indexes.CreateOneAsync(model, cancellationToken: cancellationToken);
    }
}
