using ControlApi.Models;

namespace ControlApi.Services;

public interface ITrafficService
{
    // Runs the request's ordered Endpoints sequence through k6-scripts/flow.js against the real
    // stack, calling onProgress roughly once a second (parsed from k6's own periodic status lines)
    // while it runs, then returns the final report. Null return means an endpoint id didn't
    // resolve against ListKnownEndpoints (already rejected by Program.cs before this is called).
    Task<TrafficReport?> RunTrafficAsync(TrafficRequest request, Func<TrafficProgress, Task> onProgress, CancellationToken ct);

    // The real routes a traffic run's step sequence can be built from - see EndpointDefinition and
    // k6-scripts/flow.js.
    IReadOnlyList<EndpointDefinition> ListKnownEndpoints();

    // The bulk, paginated reads a run's DataPoolRequest can preload from - see DataSourceDefinition.
    IReadOnlyList<DataSourceDefinition> ListDataSources();
}
