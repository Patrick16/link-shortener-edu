using ControlApi.Models;

namespace ControlApi.Services;

public interface ILessonProbeService
{
    // Fires input.Count real requests against input.EndpointId, input.IntervalMs apart, and
    // reports the real success/failure split - see ChaosProbeInput's own comment for why this
    // exists (a chaos-trigger lesson otherwise has no real traffic running during its own outage
    // window at all).
    Task<ChaosProbeResult> RunAsync(ChaosProbeInput input, CancellationToken ct);
}
