using System.Collections.Concurrent;
using ControlApi.Models;
using Docker.DotNet.Models;

namespace ControlApi.Services;

public sealed class ContainerLifecycleService(IContainerRuntime runtime, IInfraToggleService infraToggle, ILogger<ContainerLifecycleService> logger) : IContainerLifecycleService
{
    // link-api/redirect-api scale because nginx fronts them (see sandbox/infra/nginx/nginx.conf);
    // shortener-service/traffic-service/reporting-service scale as RabbitMQ competing consumers
    // instead - no load balancer involved, RabbitMQ itself round-robins unacked deliveries across
    // every consumer on a queue. reporting-service's ClickHouse write is safe under that pattern
    // for the same reason redelivery already is (see its own architecture.json description): the
    // `clicks` table is a ReplacingMergeTree ordered by (hash, id), so two replicas both getting a
    // copy of the same event just produces rows that collapse into one on the next background
    // merge, not a correctness bug. Still an explicit allowlist, not "anything in the compose
    // file", so a stateful/singleton service (postgres, rabbitmq, pgcat, ...) isn't even an option
    // to try by mistake - all three workers are volume-free, same property ScaleAsync already
    // relies on for --force-recreate safety elsewhere (see IContainerRuntime.RunComposeAsync's own
    // comment). architecture.json already declared reporting-service's "scalable" capability ahead
    // of this list actually including it - the UI's scale control existed but every call failed
    // with "is not a scalable service" until now (found during review).
    private static readonly IReadOnlyList<string> ScalableServices = ["link-api", "redirect-api", "shortener-service", "traffic-service", "reporting-service"];

    // The two RabbitMQ consumers - mirrors InfraToggleService's own RabbitMqConsumingServices list.
    // Duplicated rather than shared: it's a stable domain fact (which services are RabbitMQ
    // consumers), not mutable state, and ScaleAsync only needs it to know whether to forward the
    // current prefetch value on a scale-up - not worth a cross-service dependency just to read a
    // 2-item constant.
    private static readonly IReadOnlyList<string> RabbitMqConsumingServices = ["shortener-service", "traffic-service"];

    public async Task<IReadOnlyList<ManagedContainer>> ListContainersAsync(CancellationToken ct)
    {
        var containers = await runtime.Client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [$"com.docker.compose.project={runtime.ComposeProject}"] = true },
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
        var container = await runtime.FindAsync(serviceId, ct);
        if (container is null)
        {
            return null;
        }

        logger.LogWarning("Stopping container {ServiceId} ({ContainerId})", serviceId, container.ID);
        await runtime.Client.Containers.StopContainerAsync(container.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct);
        return await runtime.FindAsync(serviceId, ct) is { } updated ? ToStatus(updated) : null;
    }

    public async Task<ManagedContainer?> StartAsync(string serviceId, CancellationToken ct)
    {
        var container = await runtime.FindAsync(serviceId, ct);
        if (container is null)
        {
            return null;
        }

        logger.LogWarning("Starting container {ServiceId} ({ContainerId})", serviceId, container.ID);
        await runtime.Client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), ct);
        return await runtime.FindAsync(serviceId, ct) is { } updated ? ToStatus(updated) : null;
    }

    public async Task<ManagedContainer?> RestartAsync(string serviceId, CancellationToken ct)
    {
        var container = await runtime.FindAsync(serviceId, ct);
        if (container is null)
        {
            return null;
        }

        logger.LogWarning("Restarting container {ServiceId} ({ContainerId})", serviceId, container.ID);
        await runtime.Client.Containers.RestartContainerAsync(container.ID, new ContainerRestartParameters { WaitBeforeKillSeconds = 10 }, ct);
        return await runtime.FindAsync(serviceId, ct) is { } updated ? ToStatus(updated) : null;
    }

    public async Task<ResourceSample?> GetResourceSampleAsync(ManagedContainer container, CancellationToken ct)
    {
        if (container.State != "running")
        {
            return null;
        }

        try
        {
            // Stream = false still returns one response with both cpu_stats and precpu_stats
            // populated (two samples internally), which is what the CPU% formula below needs -
            // same as what `docker stats --no-stream` does under the hood.
            ContainerStatsResponse? stats = null;
            var statsTask = runtime.Client.Containers.GetContainerStatsAsync(
                container.ContainerId,
                new ContainerStatsParameters { Stream = false },
                new Progress<ContainerStatsResponse>(s => stats = s),
                ct);
            // Started alongside the stats call, not after it - GetCachedTcpConnectionCountAsync's
            // own docker-exec round trip used to run strictly after GetContainerStatsAsync finished,
            // lengthening this container's critical path by its full duration on every tick. Running
            // them concurrently means the slower of the two, not their sum, is what this task waits on.
            var tcpTask = GetCachedTcpConnectionCountAsync(container.ContainerId, ct);
            await Task.WhenAll(statsTask, tcpTask);

            if (stats is null)
            {
                return null;
            }

            var cpuDelta = (double)(stats.CPUStats.CPUUsage.TotalUsage - stats.PreCPUStats.CPUUsage.TotalUsage);
            var systemDelta = (double)(stats.CPUStats.SystemUsage - stats.PreCPUStats.SystemUsage);
            // On cgroup v2, OnlineCPUs can come back 0 with PercpuUsage also unset (null) rather
            // than populated - fall back to 1 rather than dereferencing a null PercpuUsage.
            var onlineCpus = stats.CPUStats.OnlineCPUs > 0
                ? stats.CPUStats.OnlineCPUs
                : (uint)(stats.CPUStats.CPUUsage.PercpuUsage?.Count ?? 1);
            var cpuPercent = systemDelta > 0 && cpuDelta > 0 ? cpuDelta / systemDelta * onlineCpus * 100.0 : 0.0;
            var tcpConnections = tcpTask.Result;

            return new ResourceSample(
                container.ServiceId,
                container.ContainerId,
                container.ContainerNumber,
                cpuPercent,
                (long)stats.MemoryStats.Usage,
                (long)stats.MemoryStats.Limit,
                tcpConnections,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A container that gets removed between ListContainersAsync and here (mid scale-down/
            // recreate) is routine, not exceptional - the poller runs every container's sample
            // fetch in one Task.WhenAll, so letting this propagate would drop every other
            // container's sample for the whole tick too.
            logger.LogDebug(ex, "Failed to fetch resource stats for container {ContainerId} - skipping this tick", container.ContainerId);
            return null;
        }
    }

    // A TCP connection count is only ever displayed in PinnedMetrics' overlay (see NodePanel.tsx's
    // own comment - it's not shown per-node in the diagram at all), where a reading refreshing every
    // 10s instead of every 2s isn't something a user would ever actually notice. The docker-exec
    // behind it is not free though: GetResourceSampleAsync runs once per running container every 2s
    // (ResourceStatsPollerService), and this exec spawns 3 new processes (sh, cat, grep) inside every
    // one of them - the poller's own pre-existing comment already documents the stats call alone as
    // "observed ~2s each against Docker Desktop on Windows", i.e. already saturating the 2s interval
    // on its own, so stacking a full extra exec onto every tick for every container turned this into
    // an unbounded, continuous per-container process-spawn cost purely to render a number nothing
    // needs faster than every few seconds. Cached per container instead, refreshed only once this
    // interval has actually elapsed - cuts real exec volume (not just latency) by ~5x.
    private static readonly TimeSpan TcpConnectionsRefreshInterval = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset SampledAt)> _tcpConnectionsCache = new();

    private async Task<int> GetCachedTcpConnectionCountAsync(string containerId, CancellationToken ct)
    {
        if (_tcpConnectionsCache.TryGetValue(containerId, out var cached) &&
            DateTimeOffset.UtcNow - cached.SampledAt < TcpConnectionsRefreshInterval)
        {
            return cached.Count;
        }

        var count = await GetTcpConnectionCountAsync(containerId, ct);
        _tcpConnectionsCache[containerId] = (count, DateTimeOffset.UtcNow);
        return count;
    }

    // Reads the container's own /proc/net/tcp[6] rather than shelling out to ss/netstat - those
    // aren't installed in every image here (alpine, debian-slim, pgcat), but procfs always is on
    // Linux. Each non-header line is one socket in any TCP state (LISTEN included), and grep -c
    // counts them without needing to parse the fixed-width columns - the header line has no ':' so
    // it's excluded for free, and grep's own "no match" exit code still leaves "0" on stdout.
    private async Task<int> GetTcpConnectionCountAsync(string containerId, CancellationToken ct)
    {
        try
        {
            var output = await runtime.ExecAsync(containerId, ["sh", "-c", "cat /proc/net/tcp /proc/net/tcp6 2>/dev/null | grep -c :"], ct);
            return int.TryParse(output.Trim(), out var count) ? count : 0;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read TCP connection count for container {ContainerId}", containerId);
            return 0;
        }
    }

    public IReadOnlyList<string> ListScalableServices() => ScalableServices;

    public Task<ScaleResult> ScaleAsync(string serviceId, int replicas, CancellationToken ct)
    {
        if (!ScalableServices.Contains(serviceId))
        {
            return Task.FromResult(new ScaleResult(serviceId, replicas, false, $"'{serviceId}' is not a scalable service"));
        }

        // Shares IContainerRuntime's toggle gate with every standing-env toggle (see
        // RunExclusiveToggleAsync's own doc comment) - not because this method reads
        // CurrentStandingEnv or BuildPreserveScaleArgsAsync itself, but because those toggle
        // handlers' own replica-count read (CountReplicasAsync) and their later --scale
        // re-assertion need this method's write to be fully serialized against, not just against
        // each other. Without this, ScaleAsync could change a service's replica count in the gap
        // between one of those handlers' read and its own later docker-compose call, which would
        // then reassert a now-stale count and silently undo the scale-up - the exact bug the gate
        // exists to prevent, just reachable via this method instead of via two toggle calls racing
        // each other.
        return runtime.RunExclusiveToggleAsync(async ct =>
        {
            // --no-recreate keeps existing replicas untouched, but any *new* replica this scale-up
            // creates is a fresh `docker compose up`, which re-renders RabbitMq__PrefetchCount from
            // ${RABBITMQ_PREFETCH:-10} in the compose file - without forwarding the value the
            // rabbitmq-prefetch control last set, new replicas would silently fall back to 10 while
            // the rest of the fleet is still running whatever was set (e.g. 1000).
            var env = RabbitMqConsumingServices.Contains(serviceId)
                ? new Dictionary<string, string> { ["RABBITMQ_PREFETCH"] = infraToggle.GetRabbitMqPrefetch().ToString() }
                : null;

            var (exitCode, output) = await runtime.RunComposeAsync(
                ["up", "-d", "--scale", $"{serviceId}={replicas}", "--no-recreate", serviceId], env, ct);

            if (exitCode != 0)
            {
                logger.LogWarning("Scaling {ServiceId} failed (exit {ExitCode}): {Output}", serviceId, exitCode, output);
            }

            return new ScaleResult(serviceId, replicas, exitCode == 0, output);
        }, ct);
    }

    private static ManagedContainer? ToStatus(ContainerListResponse container)
    {
        if (!container.Labels.TryGetValue("com.docker.compose.service", out var serviceId))
        {
            return null;
        }

        var containerNumber = container.Labels.TryGetValue("com.docker.compose.container-number", out var n) && int.TryParse(n, out var parsed)
            ? parsed
            : 1;

        return new ManagedContainer(serviceId, container.ID, container.State, container.Status, containerNumber);
    }
}
