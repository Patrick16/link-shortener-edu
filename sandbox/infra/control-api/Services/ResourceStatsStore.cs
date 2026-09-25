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

    // Container ids churn constantly here - every scale up/down, every infra toggle that recreates
    // a service, every plain container replacement mints new ids and abandons the old ones. Without
    // this, a long control-api session (or a test session that scales 1-100 replicas repeatedly)
    // would accumulate one dead ~30-sample entry per retired container forever. Called once per
    // poll tick with that tick's full container list - every key not in it belongs to a container
    // that no longer exists at all, not just one that's stopped.
    public void Prune(IReadOnlySet<string> liveContainerIds)
    {
        foreach (var containerId in _history.Keys)
        {
            if (!liveContainerIds.Contains(containerId))
            {
                _history.TryRemove(containerId, out _);
            }
        }
    }
}
