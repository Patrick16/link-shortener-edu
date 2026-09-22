namespace Infrastructure;

public interface IClickCounterService
{
    // Fire-and-forget from the caller's point of view - implementations should fail open (log and
    // return rather than throw) so a Redis outage never breaks the redirect itself.
    Task IncrementAsync(string hash, CancellationToken cancellationToken);
}
