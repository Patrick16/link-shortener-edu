using System.Collections.Concurrent;
using ControlApi.Models;

namespace ControlApi.Services;

// Peak CPU/memory per ServiceId for whatever traffic run is currently in flight. Fed live from
// ResourceStatsPollerService's own 2s tick (see the call in its ExecuteAsync) rather than read back
// from ResourceStatsStore's ring buffer afterwards, because that buffer only keeps ~60s of history
// and a run can run longer than that - by the time the run ends the peak could already be gone.
// One tracked run at a time, matching how RunTrafficAsync itself only ever runs one k6 container at
// once (see DockerService).
public class RunResourceMaxTracker
{
    private readonly ConcurrentDictionary<string, (double MaxCpu, long MaxMemoryBytes, double MaxMemoryPercent)> _maxima = new();
    private volatile bool _tracking;

    public void BeginRun()
    {
        _maxima.Clear();
        _tracking = true;
    }

    public IReadOnlyList<NodeResourceMax> EndRun()
    {
        _tracking = false;
        var result = _maxima.Select(kv => new NodeResourceMax(kv.Key, kv.Value.MaxCpu, kv.Value.MaxMemoryBytes, kv.Value.MaxMemoryPercent)).ToList();
        _maxima.Clear();
        return result;
    }

    public void Observe(IReadOnlyList<ResourceSample> samples)
    {
        if (!_tracking)
        {
            return;
        }

        foreach (var sample in samples)
        {
            var memoryPercent = sample.MemoryLimitBytes > 0
                ? sample.MemoryUsageBytes * 100.0 / sample.MemoryLimitBytes
                : 0;

            _maxima.AddOrUpdate(
                sample.ServiceId,
                _ => (sample.CpuPercent, sample.MemoryUsageBytes, memoryPercent),
                (_, current) => (
                    Math.Max(current.MaxCpu, sample.CpuPercent),
                    Math.Max(current.MaxMemoryBytes, sample.MemoryUsageBytes),
                    Math.Max(current.MaxMemoryPercent, memoryPercent)));
        }
    }
}
