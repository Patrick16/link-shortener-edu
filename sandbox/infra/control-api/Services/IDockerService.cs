using ControlApi.Models;

namespace ControlApi.Services;

public interface IDockerService
{
    Task<IReadOnlyList<ManagedContainer>> ListContainersAsync(CancellationToken ct);

    Task<ManagedContainer?> StopAsync(string serviceId, CancellationToken ct);

    Task<ManagedContainer?> StartAsync(string serviceId, CancellationToken ct);

    Task<ManagedContainer?> RestartAsync(string serviceId, CancellationToken ct);

    // Runs Pumba against the target's network - the target container itself keeps running
    // throughout, only its network behaves worse. Self-heals after DurationSeconds; returns null
    // if the target service isn't found.
    Task<ChaosAction?> DegradeAsync(string serviceId, ChaosRequest request, CancellationToken ct);

    // Stops any in-progress chaos against a service early instead of waiting out its duration.
    // Returns how many chaos containers were stopped.
    Task<int> HealAsync(string serviceId, CancellationToken ct);

    // Metadata (name + short human description) for the k6 scripts baked into this image
    // (sandbox/infra/control-api/k6-scripts/*.js).
    IReadOnlyList<TrafficScenarioInfo> ListTrafficScenarios();

    // Runs the named k6 script against the real stack, calling onProgress roughly once a second
    // (parsed from k6's own periodic status lines) while it runs, then returns the final report.
    // Null return means the scenario name didn't match a known script.
    Task<TrafficReport?> RunTrafficAsync(TrafficRequest request, Func<TrafficProgress, Task> onProgress, CancellationToken ct);

    // Current CPU/memory snapshot for one service, or null if it isn't running.
    Task<ResourceSample?> GetResourceSampleAsync(string serviceId, CancellationToken ct);

    // Names of services this instance will scale (an explicit allowlist, not "anything in
    // the compose file" - only ones nginx actually fronts have a reason to run >1 replica).
    IReadOnlyList<string> ListScalableServices();

    // Shells out to `docker compose ... up -d --scale <serviceId>=<replicas>` - the actual
    // replica-management logic is Compose's own, not reimplemented against the raw Docker API.
    Task<ScaleResult> ScaleAsync(string serviceId, int replicas, CancellationToken ct);

    // Runs `redis-cli FLUSHALL` inside the redis container via Docker's exec API - lets a demo
    // show cold-cache behavior on demand. Returns the command's own output, or null if redis isn't
    // running.
    Task<string?> FlushRedisAsync(CancellationToken ct);
}
