using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    // Only services nginx actually fronts (see sandbox/infra/nginx/nginx.conf) have a reason to
    // run more than one replica right now - an explicit allowlist rather than "anything in the
    // compose file" so scaling a stateful/singleton service (postgres, rabbitmq, ...) isn't even
    // an option to try by mistake.
    private static readonly IReadOnlyList<string> ScalableServices = ["link-api", "redirect-api"];

    private readonly DockerClient _client;
    private readonly string _composeProject;
    private readonly string _composeNetwork;
    private readonly string _composeFile;
    private readonly string _k6ScriptsDir;
    private readonly ILogger<DockerService> _logger;

    public DockerService(IConfiguration configuration, ILogger<DockerService> logger)
    {
        _logger = logger;
        _composeProject = configuration["Docker:ComposeProject"] ?? "sandbox";
        _composeNetwork = configuration["Docker:ComposeNetwork"] ?? $"{_composeProject}_default";
        _composeFile = configuration["Docker:ComposeFile"] ?? "/workspace/docker-compose.yml";
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

    private static readonly Regex K6ProgressLine = new(@"running \((\d+(?:\.\d+)?)s\), \d+/\d+ VUs, (\d+) complete", RegexOptions.Compiled);

    public async Task<TrafficReport?> RunTrafficAsync(TrafficRequest request, Func<TrafficProgress, Task> onProgress, CancellationToken ct)
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
            // grafana/k6's image runs as a non-root user by default, which can't write
            // summary.json into a directory extracted via the Docker API (its permissions don't
            // survive extraction the way a tar mode would suggest - the daemon applies its own
            // umask). Root avoids that fight entirely; this is a throwaway, self-removing
            // container with no docker.sock access, so it's a low-risk place to do it.
            User = "0:0",
            Cmd = ["run", "--no-color", "--summary-export", "/scripts/summary.json", "--vus", request.Vus.ToString(), "--duration", $"{request.DurationSeconds}s", "/scripts/scenario.js"],
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

        var output = await StreamLogsWithProgressAsync(created.ID, request.DurationSeconds, onProgress, ct);

        var inspect = await _client.Containers.InspectContainerAsync(created.ID, ct);
        var report = await ReadSummaryAsync(created.ID, request.Scenario, inspect.State.ExitCode, output, ct);
        await _client.Containers.RemoveContainerAsync(created.ID, new ContainerRemoveParameters(), ct);

        return report;
    }

    // Follows the container's combined stdout/stderr as it runs, parsing k6's own once-a-second
    // "running (Ns), X/X VUs, N complete..." status lines into progress pushes, while also
    // building up the full text for the raw-output field of the final report. Returns once the
    // stream hits EOF, which happens when k6 itself exits.
    private async Task<string> StreamLogsWithProgressAsync(string containerId, int totalSeconds, Func<TrafficProgress, Task> onProgress, CancellationToken ct)
    {
        var logStream = await _client.Containers.GetContainerLogsAsync(
            containerId,
            tty: false,
            new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = true },
            ct);

        var fullOutput = new StringBuilder();
        var pendingLine = new StringBuilder();
        var buffer = new byte[4096];
        double lastElapsed = 0;
        long lastIterations = 0;

        while (true)
        {
            var result = await logStream.ReadOutputAsync(buffer, 0, buffer.Length, ct);
            if (result.EOF || result.Count == 0)
            {
                break;
            }

            var chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
            fullOutput.Append(chunk);
            pendingLine.Append(chunk);

            var lines = pendingLine.ToString().Split('\n');
            for (var i = 0; i < lines.Length - 1; i++)
            {
                var match = K6ProgressLine.Match(lines[i]);
                if (!match.Success)
                {
                    continue;
                }

                var elapsed = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                if (elapsed > totalSeconds)
                {
                    // k6 prints one extra status line during its graceful-stop tail, past the
                    // requested duration - skip it rather than report a misleading rate spike from
                    // the tiny elapsed delta.
                    continue;
                }

                var iterations = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var elapsedDelta = elapsed - lastElapsed;
                var rate = elapsedDelta > 0 ? (iterations - lastIterations) / elapsedDelta : 0;
                lastElapsed = elapsed;
                lastIterations = iterations;

                var percent = (int)Math.Min(100, elapsed / totalSeconds * 100);
                await onProgress(new TrafficProgress((int)elapsed, totalSeconds, percent, iterations, rate));
            }

            pendingLine.Clear();
            pendingLine.Append(lines[^1]);
        }

        return fullOutput.ToString();
    }

    public async Task<ResourceSample?> GetResourceSampleAsync(string serviceId, CancellationToken ct)
    {
        var container = await FindAsync(serviceId, ct);
        if (container is null || container.State != "running")
        {
            return null;
        }

        // Stream = false still returns one response with both cpu_stats and precpu_stats
        // populated (two samples internally), which is what the CPU% formula below needs -
        // same as what `docker stats --no-stream` does under the hood.
        ContainerStatsResponse? stats = null;
        await _client.Containers.GetContainerStatsAsync(
            container.ID,
            new ContainerStatsParameters { Stream = false },
            new Progress<ContainerStatsResponse>(s => stats = s),
            ct);

        if (stats is null)
        {
            return null;
        }

        var cpuDelta = (double)(stats.CPUStats.CPUUsage.TotalUsage - stats.PreCPUStats.CPUUsage.TotalUsage);
        var systemDelta = (double)(stats.CPUStats.SystemUsage - stats.PreCPUStats.SystemUsage);
        var onlineCpus = stats.CPUStats.OnlineCPUs > 0 ? stats.CPUStats.OnlineCPUs : (uint)stats.CPUStats.CPUUsage.PercpuUsage.Count;
        var cpuPercent = systemDelta > 0 && cpuDelta > 0 ? cpuDelta / systemDelta * onlineCpus * 100.0 : 0.0;

        return new ResourceSample(serviceId, cpuPercent, (long)stats.MemoryStats.Usage, (long)stats.MemoryStats.Limit, DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<string> ListScalableServices() => ScalableServices;

    public async Task<ScaleResult> ScaleAsync(string serviceId, int replicas, CancellationToken ct)
    {
        if (!ScalableServices.Contains(serviceId))
        {
            return new ScaleResult(serviceId, replicas, false, $"'{serviceId}' is not a scalable service");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "compose", "-p", _composeProject, "-f", _composeFile, "up", "-d", "--scale", $"{serviceId}={replicas}", "--no-recreate", serviceId })
        {
            psi.ArgumentList.Add(arg);
        }

        _logger.LogWarning("Scaling {ServiceId} to {Replicas} replicas", serviceId, replicas);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the docker compose process");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = await stdoutTask + await stderrTask;

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("Scaling {ServiceId} failed (exit {ExitCode}): {Output}", serviceId, process.ExitCode, output);
        }

        return new ScaleResult(serviceId, replicas, process.ExitCode == 0, output);
    }

    private async Task<TrafficReport> ReadSummaryAsync(string containerId, string scenario, long exitCode, string rawOutput, CancellationToken ct)
    {
        try
        {
            var response = await _client.Containers.GetArchiveFromContainerAsync(
                containerId, new GetArchiveFromContainerParameters { Path = "/scripts/summary.json" }, false, ct);

            // Buffer fully before handing to TarReader - it does non-sequential-looking reads via
            // SubReadStream over each entry's data, which doesn't play well with the raw chunked
            // HTTP stream the Docker API hands back (hit EndOfStreamException reading directly).
            using var buffered = new MemoryStream();
            await response.Stream.CopyToAsync(buffered, ct);
            buffered.Position = 0;

            using var reader = new TarReader(buffered);
            TarEntry? entry;
            while ((entry = await reader.GetNextEntryAsync(cancellationToken: ct)) is not null)
            {
                if (entry.DataStream is null)
                {
                    continue;
                }

                using var streamReader = new StreamReader(entry.DataStream);
                var json = await streamReader.ReadToEndAsync(ct);
                return ParseSummary(json, scenario, exitCode, rawOutput);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read k6 summary.json for {Scenario} - falling back to raw output only", scenario);
        }

        return new TrafficReport(scenario, exitCode, 0, 0, 0, 0, 0, 0, 0, null, [], rawOutput);
    }

    private static TrafficReport ParseSummary(string json, string scenario, long exitCode, string rawOutput)
    {
        using var doc = JsonDocument.Parse(json);
        var metrics = doc.RootElement.GetProperty("metrics");

        long GetCount(string metric) =>
            metrics.TryGetProperty(metric, out var m) && m.TryGetProperty("count", out var c) ? c.GetInt64() : 0;
        double GetRate(string metric) =>
            metrics.TryGetProperty(metric, out var m) && m.TryGetProperty("rate", out var r) ? r.GetDouble() : 0;
        double GetValue(string metric) =>
            metrics.TryGetProperty(metric, out var m) && m.TryGetProperty("value", out var v) ? v.GetDouble() : 0;
        // http_req_failed is a k6 Rate metric: "passes" is the count of samples where the request
        // was considered failed (value 1), not a count of successes - the passes/fails naming is
        // generic to Rate metrics (also used by "checks", where 1 *does* mean success) and doesn't
        // flip meaning per-metric, so this needs to be read deliberately rather than by the names.
        long GetFailedCount(string metric) =>
            metrics.TryGetProperty(metric, out var m) && m.TryGetProperty("passes", out var p) ? p.GetInt64() : 0;

        LatencyStats? duration = null;
        if (metrics.TryGetProperty("http_req_duration", out var d))
        {
            double Field(string name) => d.TryGetProperty(name, out var v) ? v.GetDouble() : 0;
            duration = new LatencyStats(Field("avg"), Field("min"), Field("med"), Field("max"), Field("p(90)"), Field("p(95)"));
        }

        var checks = new List<CheckResult>();
        if (doc.RootElement.TryGetProperty("root_group", out var rootGroup) && rootGroup.TryGetProperty("checks", out var checksObj))
        {
            foreach (var check in checksObj.EnumerateObject())
            {
                var passes = check.Value.TryGetProperty("passes", out var p) ? p.GetInt32() : 0;
                var fails = check.Value.TryGetProperty("fails", out var f) ? f.GetInt32() : 0;
                checks.Add(new CheckResult(check.Name, passes, fails));
            }
        }

        return new TrafficReport(
            scenario,
            exitCode,
            GetCount("http_reqs"),
            GetRate("http_reqs"),
            GetFailedCount("http_req_failed"),
            GetValue("http_req_failed"),
            GetCount("iterations"),
            GetRate("iterations"),
            (int)GetValue("vus_max"),
            duration,
            checks,
            rawOutput);
    }

    private static async Task<MemoryStream> BuildScriptTarAsync(string scriptPath, CancellationToken ct)
    {
        var tarStream = new MemoryStream();
        await using (var writer = new TarWriter(tarStream, TarEntryFormat.Pax, leaveOpen: true))
        {
            // Without an explicit directory entry, the extraction still creates "scripts/" (to
            // hold scenario.js) but with a restrictive default mode - grafana/k6 runs as a
            // non-root user and needs write access on this directory itself to later create
            // summary.json there, not just read access to the script file inside it.
            var dirEntry = new PaxTarEntry(TarEntryType.Directory, "scripts/") { Mode = (UnixFileMode)0777 };
            await writer.WriteEntryAsync(dirEntry, ct);

            var fileEntry = new PaxTarEntry(TarEntryType.RegularFile, "scripts/scenario.js")
            {
                DataStream = File.OpenRead(scriptPath),
                Mode = (UnixFileMode)0644,
            };
            await writer.WriteEntryAsync(fileEntry, ct);
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

        var containerNumber = container.Labels.TryGetValue("com.docker.compose.container-number", out var n) && int.TryParse(n, out var parsed)
            ? parsed
            : 1;

        return new ManagedContainer(serviceId, container.ID, container.State, container.Status, containerNumber);
    }
}
