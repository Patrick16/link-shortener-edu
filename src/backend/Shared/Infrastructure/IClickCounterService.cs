namespace Infrastructure;

public interface IClickCounterService
{
    // Fire-and-forget from the caller's point of view - implementations should fail open (log and
    // return rather than throw) so a Redis outage never breaks the redirect itself. ownerId is null
    // for anonymous links, which have no per-user top list to feed.
    Task RecordClickAsync(string hash, Guid? ownerId, DateTime clickedAtUtc, CancellationToken cancellationToken);
}
