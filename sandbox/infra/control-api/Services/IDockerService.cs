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

    // Names of the k6 scripts baked into this image (sandbox/infra/control-api/k6-scripts/*.js).
    IReadOnlyList<string> ListTrafficScenarios();

    // Runs the named k6 script against the real stack and waits for it to finish - the request
    // blocks for up to ~DurationSeconds, which is fine for the short runs this is meant for.
    Task<TrafficResult?> RunTrafficAsync(TrafficRequest request, CancellationToken ct);
}
