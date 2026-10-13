namespace ReportingApi.Realtime;

// Counts open dashboard connections per user, so the push loop only computes a user's top list
// while somebody is actually looking at it. A user can have several tabs open, hence the count
// rather than a flag.
public sealed class TopLinksSubscriptions
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, int> _connectionsByUser = [];

    public void Connected(Guid userId)
    {
        lock (_lock)
        {
            _connectionsByUser[userId] = _connectionsByUser.GetValueOrDefault(userId) + 1;
        }
    }

    public void Disconnected(Guid userId)
    {
        lock (_lock)
        {
            if (!_connectionsByUser.TryGetValue(userId, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                _connectionsByUser.Remove(userId);
            }
            else
            {
                _connectionsByUser[userId] = count - 1;
            }
        }
    }

    public bool IsWatched(Guid userId)
    {
        lock (_lock)
        {
            return _connectionsByUser.ContainsKey(userId);
        }
    }
}
