using Docker.DotNet;
using Docker.DotNet.Models;

namespace ControlApi.Services;

public sealed class ContainerRuntime : IContainerRuntime
{
    private readonly ILogger<ContainerRuntime> _logger;

    public DockerClient Client { get; }

    public string ComposeProject { get; }

    public string ComposeNetwork { get; }

    // A netem-partitioned or paused target (chaos testing this very tool enables) can leave a
    // command like mongosh blocked on its own server-selection timeout for tens of seconds - 10s is
    // generous for the read-only status/config commands this is used for (normally under a second)
    // without masking a real hang as "just slow".
    private static readonly TimeSpan ExecTimeout = TimeSpan.FromSeconds(10);

    public ContainerRuntime(IConfiguration configuration, ILogger<ContainerRuntime> logger)
    {
        _logger = logger;
        ComposeProject = configuration["Docker:ComposeProject"] ?? "sandbox";
        ComposeNetwork = configuration["Docker:ComposeNetwork"] ?? $"{ComposeProject}_default";

        var endpoint = configuration["Docker:Endpoint"]
            ?? (OperatingSystem.IsWindows() ? "npipe://./pipe/docker_engine" : "unix:///var/run/docker.sock");
        Client = new DockerClientConfiguration(new Uri(endpoint)).CreateClient();
    }

    // Extracted as a pure function so this selection rule is unit-testable without a real/mocked
    // Docker daemon. Ordering by container-number and taking the lowest makes a scaled service's
    // selection deterministic and matches the frontend's own convention (instances[0]) - repeated
    // Stop/Start/Restart/Degrade calls against a scaled service used to risk silently acting on a
    // different physical container each time, since Docker's ListContainersAsync order isn't
    // documented/stable.
    public static ContainerListResponse? SelectPrimary(IEnumerable<ContainerListResponse> containers) =>
        containers
            .OrderBy(c => c.Labels.TryGetValue("com.docker.compose.container-number", out var n) && int.TryParse(n, out var parsed) ? parsed : int.MaxValue)
            .FirstOrDefault();

    public async Task<ContainerListResponse?> FindAsync(string serviceId, CancellationToken ct)
    {
        var containers = await Client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool>
                {
                    [$"com.docker.compose.project={ComposeProject}"] = true,
                    [$"com.docker.compose.service={serviceId}"] = true,
                },
            },
        }, ct);

        return SelectPrimary(containers);
    }

    public async Task<string> ExecAsync(string containerId, IList<string> cmd, CancellationToken ct)
    {
        var (_, output) = await ExecWithExitCodeAsync(containerId, cmd, ct);
        return output;
    }

    public async Task<(long ExitCode, string Output)> ExecWithExitCodeAsync(string containerId, IList<string> cmd, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ExecTimeout);
        try
        {
            var exec = await Client.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
            {
                Cmd = cmd,
                AttachStdout = true,
                AttachStderr = true,
            }, timeoutCts.Token);

            using var stream = await Client.Exec.StartAndAttachContainerExecAsync(exec.ID, false, timeoutCts.Token);
            var (stdout, stderr) = await stream.ReadOutputToEndAsync(timeoutCts.Token);
            var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;

            var inspect = await Client.Exec.InspectContainerExecAsync(exec.ID, timeoutCts.Token);
            return (inspect.ExitCode, output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own CancelAfter fired, not the caller's token - surface this as a plain failure
            // (TimeoutException), not OperationCanceledException, so callers/pollers that
            // deliberately let a real shutdown cancellation propagate don't mistake this timeout
            // for one and let it kill the whole poller instead of just failing this one tick.
            throw new TimeoutException($"docker exec timed out after {ExecTimeout} for container {containerId}");
        }
    }

    public async Task EnsureImageAsync(string image, CancellationToken ct)
    {
        var existing = await Client.Images.ListImagesAsync(new ImagesListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["reference"] = new Dictionary<string, bool> { [image] = true },
            },
        }, ct);

        if (existing.Count > 0)
        {
            return;
        }

        var parts = image.Split(':', 2);
        _logger.LogInformation("Pulling {Image} (first use)", image);
        await Client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = parts[0], Tag = parts.Length > 1 ? parts[1] : "latest" },
            null,
            new Progress<JSONMessage>(),
            ct);
    }

    public async Task<string?> GetContainerIpAsync(string containerId, CancellationToken ct)
    {
        var inspect = await Client.Containers.InspectContainerAsync(containerId, ct);
        return inspect.NetworkSettings?.Networks?.TryGetValue(ComposeNetwork, out var endpoint) == true ? endpoint.IPAddress : null;
    }
}
