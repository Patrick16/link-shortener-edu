using ControlApi.Models;

namespace ControlApi.Services;

public interface IContainerLifecycleService
{
    Task<IReadOnlyList<ManagedContainer>> ListContainersAsync(CancellationToken ct);

    Task<ManagedContainer?> StopAsync(string serviceId, CancellationToken ct);

    Task<ManagedContainer?> StartAsync(string serviceId, CancellationToken ct);

    Task<ManagedContainer?> RestartAsync(string serviceId, CancellationToken ct);

    // Current CPU/memory/TCP snapshot for one specific container - takes the ManagedContainer
    // itself (already has its ContainerId) rather than a serviceId, so a caller sampling every
    // replica of a scaled service gets each container's own reading instead of every call
    // re-resolving "the" container for that service and landing on the same one each time.
    Task<ResourceSample?> GetResourceSampleAsync(ManagedContainer container, CancellationToken ct);

    // Names of services this instance will scale (an explicit allowlist, not "anything in
    // the compose file" - only ones nginx actually fronts have a reason to run >1 replica).
    IReadOnlyList<string> ListScalableServices();

    // Shells out to `docker compose ... up -d --scale <serviceId>=<replicas>` - the actual
    // replica-management logic is Compose's own, not reimplemented against the raw Docker API.
    Task<ScaleResult> ScaleAsync(string serviceId, int replicas, CancellationToken ct);
}
