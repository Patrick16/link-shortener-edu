namespace ReportingApi.Realtime;

// Decides which users are due a fresh top-links push. A user changed since their last push is
// "dirty"; a dirty user is only due once minInterval has passed since their previous push. So a
// burst of clicks produces at most one push per interval, and the last change in a burst still
// gets sent - it simply waits in the dirty set until its turn comes around.
public sealed class TopLinksPushScheduler
{
    private readonly object _lock = new();
    private readonly HashSet<Guid> _dirty = [];
    private readonly Dictionary<Guid, DateTime> _lastPushedUtc = [];
    private readonly Dictionary<Guid, string> _lastPayload = [];

    // Payload of the last push sent to this user, so an unchanged list is not pushed again.
    public bool IsUnchanged(Guid userId, string payload)
    {
        lock (_lock)
        {
            return _lastPayload.TryGetValue(userId, out var previous) && previous == payload;
        }
    }

    public void RecordPayload(Guid userId, string payload)
    {
        lock (_lock)
        {
            _lastPayload[userId] = payload;
        }
    }

    public void MarkDirty(Guid userId)
    {
        lock (_lock)
        {
            _dirty.Add(userId);
        }
    }

    public IReadOnlyList<Guid> TakeDue(DateTime nowUtc, TimeSpan minInterval)
    {
        lock (_lock)
        {
            var due = _dirty
                .Where(userId => !_lastPushedUtc.TryGetValue(userId, out var last) || nowUtc - last >= minInterval)
                .ToList();

            foreach (var userId in due)
            {
                _dirty.Remove(userId);
                _lastPushedUtc[userId] = nowUtc;
            }

            return due;
        }
    }

    // Called when the user's last connection closes. Drops all per-user state, so nothing accumulates
    // for users who have gone away, and a later reconnect gets its first push immediately rather
    // than waiting out an old window.
    public void Forget(Guid userId)
    {
        lock (_lock)
        {
            _dirty.Remove(userId);
            _lastPushedUtc.Remove(userId);
            _lastPayload.Remove(userId);
        }
    }
}
