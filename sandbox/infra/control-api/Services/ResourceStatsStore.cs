using System.Collections.Concurrent;
using ControlApi.Models;

namespace ControlApi.Services;

// In-memory ring buffer per container - 60s of history at the poller's 2s interval. Lost on
// restart, which is fine: this is "what's happening right now", not a metrics backend. Keyed by
// ContainerId rather than ServiceId - a scaled service has one history per replica, not one shared
// history that different containers' samples would otherwise overwrite/interleave into.
public class ResourceStatsStore
{
    private const int MaxSamples = 30;
    private readonly ConcurrentDictionary<string, LinkedList<ResourceSample>> _history = new();

    public void Add(ResourceSample sample)
    {
        var list = _history.GetOrAdd(sample.ContainerId, _ => new LinkedList<ResourceSample>());
        lock (list)
        {
            list.AddLast(sample);
            while (list.Count > MaxSamples)
            {
                list.RemoveFirst();
            }
        }
    }

    public IReadOnlyList<ResourceSample> GetHistory(string containerId)
    {
        if (!_history.TryGetValue(containerId, out var list))
        {
            return [];
        }

        lock (list)
        {
            return list.ToList();
        }
    }
}
