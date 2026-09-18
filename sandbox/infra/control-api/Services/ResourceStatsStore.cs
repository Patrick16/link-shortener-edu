using System.Collections.Concurrent;
using ControlApi.Models;

namespace ControlApi.Services;

// In-memory ring buffer per service - 60s of history at the poller's 2s interval. Lost on
// restart, which is fine: this is "what's happening right now", not a metrics backend.
public class ResourceStatsStore
{
    private const int MaxSamples = 30;
    private readonly ConcurrentDictionary<string, LinkedList<ResourceSample>> _history = new();

    public void Add(ResourceSample sample)
    {
        var list = _history.GetOrAdd(sample.ServiceId, _ => new LinkedList<ResourceSample>());
        lock (list)
        {
            list.AddLast(sample);
            while (list.Count > MaxSamples)
            {
                list.RemoveFirst();
            }
        }
    }

    public IReadOnlyList<ResourceSample> GetHistory(string serviceId)
    {
        if (!_history.TryGetValue(serviceId, out var list))
        {
            return [];
        }

        lock (list)
        {
            return list.ToList();
        }
    }
}
