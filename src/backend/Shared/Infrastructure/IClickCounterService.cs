namespace Infrastructure;

public interface IClickCounterService
{
    // Fire-and-forget from the caller's point of view - implementations should fail open (log and
    // return rather than throw) so a Redis outage never breaks the redirect itself.
    Task IncrementAsync(string hash, CancellationToken cancellationToken);

    // Null means "no data" (cache miss or a Redis failure), never zero - callers combine this with
    // their own durable count rather than treating null as an actual zero click count (see
    // LinksController.GetLinks).
    Task<long?> GetAsync(string hash, CancellationToken cancellationToken);

    // Batched - one round trip for a whole page of hashes instead of one per hash. A hash with no
    // entry (miss or failure) is simply absent from the result, same "absent means no data" contract
    // as GetAsync.
    Task<IReadOnlyDictionary<string, long>> GetManyAsync(IReadOnlyCollection<string> hashes, CancellationToken cancellationToken);
}
