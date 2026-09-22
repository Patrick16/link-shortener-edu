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

    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<ClickMeta> _collection;

    public MongoClickMetaStore(string connectionString)
    {
        var url = MongoUrl.Create(connectionString);
        _database = new MongoClient(url).GetDatabase(url.DatabaseName);
        _collection = _database.GetCollection<ClickMeta>(CollectionName);
    }

    public Task SaveAsync(ClickMeta meta, CancellationToken cancellationToken = default) =>
        // Upsert by Id rather than Insert: redelivery-safe on its own, independent of the
        // "already stored in Postgres" check in ClickTrackedConsumer (defense in depth — the two
        // writes aren't transactional with each other).
        _collection.ReplaceOneAsync(
            x => x.Id == meta.Id,
            meta,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);

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
}
