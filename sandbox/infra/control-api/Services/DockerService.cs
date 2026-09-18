using System.Formats.Tar;
using ControlApi.Models;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace ControlApi.Services;

// Every lookup filters on both the compose service label AND the compose project label, so this
// can never reach a container outside the sandbox stack it's meant to control - even if a caller
// passes an unexpected serviceId, Docker's own label filter (exact match) just returns nothing.
public class DockerService : IDockerService
{
    // gaiadocker/iproute2 is Pumba's own default tc-image (has the `tc` binary Pumba needs inside
    // the target's network namespace) - pinned explicitly rather than relying on Pumba's default
    // so the image we pre-pull always matches the one Pumba will actually ask Docker to run.
    private const string PumbaImage = "gaiaadm/pumba:latest";
    private const string TcImage = "gaiadocker/iproute2:latest";
    private const string ChaosTargetLabel = "control-api.chaos-target";
    private const string K6Image = "grafana/k6:latest";

    private readonly DockerClient _client;
    private readonly string _composeProject;
    private readonly string _composeNetwork;
    private readonly string _k6ScriptsDir;
    private readonly ILogger<DockerService> _logger;

    public DockerService(IConfiguration configuration, ILogger<DockerService> logger)
    {
        _logger = logger;
        _composeProject = configuration["Docker:ComposeProject"] ?? "sandbox";
        _composeNetwork = configuration["Docker:ComposeNetwork"] ?? $"{_composeProject}_default";
        _k6ScriptsDir = configuration["K6:ScriptsDir"] ?? Path.Combine(AppContext.BaseDirectory, "k6-scripts");

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

    public async Task<ChaosAction?> DegradeAsync(string serviceId, ChaosRequest request, CancellationToken ct)
    {
        var target = await FindAsync(serviceId, ct);
        if (target is null)
        {
            return null;
        }

        await EnsureImageAsync(PumbaImage, ct);
        await EnsureImageAsync(TcImage, ct);

        var netemArgs = request.Type switch
        {
            ChaosType.Delay => new[] { "delay", "--time", request.Amount.ToString() },
            ChaosType.Loss => new[] { "loss", "--percent", request.Amount.ToString() },
            ChaosType.Partition => new[] { "loss", "--percent", "100" },
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };

        var cmd = new List<string> { "netem", "--tc-image", TcImage, "--duration", $"{request.DurationSeconds}s" };
        cmd.AddRange(netemArgs);
        cmd.Add(target.ID);

        var created = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = PumbaImage,
            Cmd = cmd,
            Labels = new Dictionary<string, string>
            {
                ["com.docker.compose.project"] = _composeProject,
                [ChaosTargetLabel] = serviceId,
            },
            HostConfig = new HostConfig
            {
                Binds = ["/var/run/docker.sock:/var/run/docker.sock"],
                AutoRemove = true,
            },
        }, ct);

        _logger.LogWarning(
            "Starting {ChaosType} chaos against {ServiceId} for {Duration}s (pumba container {PumbaId})",
            request.Type, serviceId, request.DurationSeconds, created.ID);
        await _client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);

        return new ChaosAction(serviceId, created.ID, request.Type, request.DurationSeconds, DateTimeOffset.UtcNow);
    }

    public async Task<int> HealAsync(string serviceId, CancellationToken ct)
    {
        var chaosContainers = await _client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [$"{ChaosTargetLabel}={serviceId}"] = true },
            },
        }, ct);

        foreach (var chaos in chaosContainers)
        {
            _logger.LogWarning("Healing {ServiceId} early - stopping chaos container {ChaosId}", serviceId, chaos.ID);
            // Pumba's netem handler traps SIGTERM to tear down the tc rule before exiting, so a
            // plain stop (not a kill) is what lets the target's network actually recover.
            await _client.Containers.StopContainerAsync(chaos.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct);
        }

        return chaosContainers.Count;
    }

    public IReadOnlyList<string> ListTrafficScenarios()
    {
        if (!Directory.Exists(_k6ScriptsDir))
        {
            return [];
        }

        return Directory.GetFiles(_k6ScriptsDir, "*.js")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<TrafficResult?> RunTrafficAsync(TrafficRequest request, CancellationToken ct)
    {
        var scriptPath = Path.Combine(_k6ScriptsDir, $"{request.Scenario}.js");
        if (!File.Exists(scriptPath))
        {
            return null;
        }

        await EnsureImageAsync(K6Image, ct);

        var created = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = K6Image,
            Cmd = ["run", "--no-color", "--vus", request.Vus.ToString(), "--duration", $"{request.DurationSeconds}s", "/scripts/scenario.js"],
            Labels = new Dictionary<string, string> { ["com.docker.compose.project"] = _composeProject },
            HostConfig = new HostConfig { NetworkMode = _composeNetwork },
        }, ct);

        await using (var tarStream = await BuildScriptTarAsync(scriptPath, ct))
        {
            // The destination itself must already exist for the Docker API to accept the
            // extraction - "/" always does, and the tar entry's own "scripts/scenario.js" path
            // makes the extraction create that subdirectory as it unpacks.
            await _client.Containers.ExtractArchiveToContainerAsync(
                created.ID,
                new ContainerPathStatParameters { Path = "/" },
                tarStream,
                ct);
        }

        _logger.LogWarning(
            "Running k6 scenario {Scenario} ({Vus} VUs, {Duration}s) as {ContainerId}",
            request.Scenario, request.Vus, request.DurationSeconds, created.ID);

        await _client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
        await _client.Containers.WaitContainerAsync(created.ID, ct);

        var logStream = await _client.Containers.GetContainerLogsAsync(
            created.ID,
            tty: false,
            new ContainerLogsParameters { ShowStdout = true, ShowStderr = true },
            ct);
        var (stdout, stderr) = await logStream.ReadOutputToEndAsync(ct);

        var inspect = await _client.Containers.InspectContainerAsync(created.ID, ct);
        await _client.Containers.RemoveContainerAsync(created.ID, new ContainerRemoveParameters(), ct);

        return new TrafficResult(request.Scenario, inspect.State.ExitCode, stdout + stderr);
    }

    private static async Task<MemoryStream> BuildScriptTarAsync(string scriptPath, CancellationToken ct)
    {
        var tarStream = new MemoryStream();
        await using (var writer = new TarWriter(tarStream, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "scripts/scenario.js")
            {
                DataStream = File.OpenRead(scriptPath),
            };
            await writer.WriteEntryAsync(entry, ct);
        }

        tarStream.Position = 0;
        return tarStream;
    }

    private async Task EnsureImageAsync(string image, CancellationToken ct)
    {
        var existing = await _client.Images.ListImagesAsync(new ImagesListParameters
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
        await _client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = parts[0], Tag = parts.Length > 1 ? parts[1] : "latest" },
            null,
            new Progress<JSONMessage>(),
            ct);
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
