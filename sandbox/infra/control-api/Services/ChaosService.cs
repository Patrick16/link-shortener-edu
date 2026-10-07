using ControlApi.Models;
using Docker.DotNet.Models;

namespace ControlApi.Services;

public sealed class ChaosService(IContainerRuntime runtime, ILogger<ChaosService> logger) : IChaosService
{
    // gaiadocker/iproute2 is Pumba's own default tc-image (has the `tc` binary Pumba needs inside
    // the target's network namespace) - pinned explicitly rather than relying on Pumba's default
    // so the image we pre-pull always matches the one Pumba will actually ask Docker to run.
    private const string PumbaImage = "gaiaadm/pumba:latest";
    private const string TcImage = "gaiadocker/iproute2:latest";
    private const string ChaosTargetLabel = "control-api.chaos-target";

    public async Task<ChaosAction?> DegradeAsync(string serviceId, ChaosRequest request, CancellationToken ct)
    {
        var target = await runtime.FindAsync(serviceId, ct);
        if (target is null)
        {
            return null;
        }

        await runtime.EnsureImageAsync(PumbaImage, ct);
        await runtime.EnsureImageAsync(TcImage, ct);

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

        var created = await runtime.Client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = PumbaImage,
            Cmd = cmd,
            Labels = new Dictionary<string, string>
            {
                ["com.docker.compose.project"] = runtime.ComposeProject,
                [ChaosTargetLabel] = serviceId,
            },
            HostConfig = new HostConfig
            {
                Binds = ["/var/run/docker.sock:/var/run/docker.sock"],
                AutoRemove = true,
            },
        }, ct);

        logger.LogWarning(
            "Starting {ChaosType} chaos against {ServiceId} for {Duration}s (pumba container {PumbaId})",
            request.Type, serviceId, request.DurationSeconds, created.ID);
        await runtime.Client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);

        return new ChaosAction(serviceId, created.ID, request.Type, request.DurationSeconds, DateTimeOffset.UtcNow);
    }

    public async Task<int> HealAsync(string serviceId, CancellationToken ct)
    {
        var chaosContainers = await runtime.Client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [$"{ChaosTargetLabel}={serviceId}"] = true },
            },
        }, ct);

        foreach (var chaos in chaosContainers)
        {
            logger.LogWarning("Healing {ServiceId} early - stopping chaos container {ChaosId}", serviceId, chaos.ID);
            // Pumba's netem handler traps SIGTERM to tear down the tc rule before exiting, so a
            // plain stop (not a kill) is what lets the target's network actually recover.
            await runtime.Client.Containers.StopContainerAsync(chaos.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct);
        }

        return chaosContainers.Count;
    }
}
