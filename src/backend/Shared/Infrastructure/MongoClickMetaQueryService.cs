using Common.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Infrastructure;

// Read side of clicks_meta_db - same connection/collection setup as MongoClickMetaStore, but a
// separate class (not just a second method on it) so the IClickMetaStore/IClickMetaQueryService
// split is real at the implementation level too, matching ClickHouseClickFactStore/
// ClickHouseClickFactQueryService.
public sealed class MongoClickMetaQueryService : IClickMetaQueryService
{
    private const string CollectionName = "clicks";

    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<ClickMeta> _collection;

    public MongoClickMetaQueryService(string connectionString)
    {
        var url = MongoUrl.Create(connectionString);
        _database = new MongoClient(url).GetDatabase(url.DatabaseName);
        _collection = _database.GetCollection<ClickMeta>(CollectionName);
    }

    public async Task<IReadOnlyList<ClickDetail>> GetRecentAsync(
        string hash, Guid callerUserId, int limit, CancellationToken cancellationToken = default)
    {
        var filter = Builders<ClickMeta>.Filter.Eq(x => x.Hash, hash) &
                     Builders<ClickMeta>.Filter.Eq(x => x.UserId, callerUserId);

        // Served by the (Hash, UserId, ClickedAt desc) compound index MongoClickMetaStore creates -
        // without it this would be a full collection scan per request.
        var docs = await _collection.Find(filter)
            .SortByDescending(x => x.ClickedAt)
            .Limit(limit)
            .ToListAsync(cancellationToken);

        return docs
            .Select(x => new ClickDetail(x.Id, x.ClickedAt, x.UserAgent, x.Referrer, x.Browser, x.Os, x.DeviceType, x.Country, x.City))
            .ToList();
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
}
