using ControlApi.Models;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace ControlApi.Services;

// Every lookup filters on both the compose service label AND the compose project label, so this
// can never reach a container outside the sandbox stack it's meant to control - even if a caller
// passes an unexpected serviceId, Docker's own label filter (exact match) just returns nothing.
public class DockerService : IDockerService
{
    private readonly DockerClient _client;
    private readonly string _composeProject;
    private readonly ILogger<DockerService> _logger;

    public DockerService(IConfiguration configuration, ILogger<DockerService> logger)
    {
        _logger = logger;
        _composeProject = configuration["Docker:ComposeProject"] ?? "sandbox";

        var endpoint = configuration["Docker:Endpoint"]
            ?? (OperatingSystem.IsWindows() ? "npipe://./pipe/docker_engine" : "unix:///var/run/docker.sock");
        _client = new DockerClientConfiguration(new Uri(endpoint)).CreateClient();
    }

    public async Task<IReadOnlyList<ManagedContainer>> ListContainersAsync(CancellationToken ct)
    {
        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [$"com.docker.compose.project={_composeProject}"] = true },
            },
        }, ct);

        return containers
            .Select(ToStatus)
            .Where(status => status is not null)
            .Select(status => status!)
            .ToList();
    }

    public async Task<ManagedContainer?> StopAsync(string serviceId, CancellationToken ct)
    {
        var container = await FindAsync(serviceId, ct);
        if (container is null)
        {
            return null;
        }

        _logger.LogWarning("Stopping container {ServiceId} ({ContainerId})", serviceId, container.ID);
        await _client.Containers.StopContainerAsync(container.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct);
        return await FindAsync(serviceId, ct) is { } updated ? ToStatus(updated) : null;
    }

    public async Task<ManagedContainer?> StartAsync(string serviceId, CancellationToken ct)
    {
        var container = await FindAsync(serviceId, ct);
        if (container is null)
        {
            return null;
        }

        _logger.LogWarning("Starting container {ServiceId} ({ContainerId})", serviceId, container.ID);
        await _client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), ct);
        return await FindAsync(serviceId, ct) is { } updated ? ToStatus(updated) : null;
    }

    public async Task<ManagedContainer?> RestartAsync(string serviceId, CancellationToken ct)
    {
        var container = await FindAsync(serviceId, ct);
        if (container is null)
        {
            return null;
        }

        _logger.LogWarning("Restarting container {ServiceId} ({ContainerId})", serviceId, container.ID);
        await _client.Containers.RestartContainerAsync(container.ID, new ContainerRestartParameters { WaitBeforeKillSeconds = 10 }, ct);
        return await FindAsync(serviceId, ct) is { } updated ? ToStatus(updated) : null;
    }

    private async Task<ContainerListResponse?> FindAsync(string serviceId, CancellationToken ct)
    {
        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool>
                {
                    [$"com.docker.compose.project={_composeProject}"] = true,
                    [$"com.docker.compose.service={serviceId}"] = true,
                },
            },
        }, ct);

        return containers.FirstOrDefault();
    }

    private static ManagedContainer? ToStatus(ContainerListResponse container)
    {
        if (!container.Labels.TryGetValue("com.docker.compose.service", out var serviceId))
        {
            return null;
        }

        return new ManagedContainer(serviceId, container.ID, container.State, container.Status);
    }
}
