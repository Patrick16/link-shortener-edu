namespace Common
{
    public interface IEntityCacheService<T> where T : class
    {
        Task CacheAsync(T entity, string id, CancellationToken cancellationToken, int ttlSeconds = 3600);
        Task<T?> GetCachedAsync(string id, CancellationToken cancellationToken);
        Task<T?> GetOrFetch(string id, Func<Task<T?>> fetchFromDb, CancellationToken cancellationToken);
        Task InvalidateCacheAsync(string id);
    }
}