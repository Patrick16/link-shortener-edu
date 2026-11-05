using System.Formats.Tar;
using System.Text;
using ControlApi.Models;
using Docker.DotNet.Models;

namespace ControlApi.Services.K6;

// The Docker-container-lifecycle half of running a k6 scenario - create/extract/start/stream/
// inspect/remove, plus cleanup on failure. TrafficService still owns resolving the request's steps,
// building the k6 command/env, and the optional data-pool preload; this class only knows how to
// actually run a k6 container once those are ready and hand back the parsed report. Pure move out
// of TrafficService, same behavior - see .notes/PLAN.md's Simplification Backlog item 3.
public sealed class K6ContainerRunner(IContainerRuntime runtime, ILogger<K6ContainerRunner> logger)
{
    // Internal, not private: Program.cs's /health/ready check reads this too, so the image name
    // stays defined in exactly one place.
    internal const string K6Image = "grafana/k6:latest";

    public async Task<TrafficReport?> RunAsync(
        TrafficRequest request,
        List<string> cmd,
        List<string> env,
        string scriptPath,
        int totalSeconds,
        int? targetIterations,
        IReadOnlyList<string> stepIds,
        Func<TrafficProgress, Task> onProgress,
        CancellationToken ct)
    {
        await runtime.EnsureImageAsync(K6Image, ct);

        var created = await runtime.Client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = K6Image,
            // grafana/k6's image runs as a non-root user by default, which can't write
            // summary.json into a directory extracted via the Docker API (its permissions don't
            // survive extraction the way a tar mode would suggest - the daemon applies its own
            // umask). Root avoids that fight entirely; this is a throwaway, self-removing
            // container with no docker.sock access, so it's a low-risk place to do it.
            User = "0:0",
            Cmd = cmd,
            Env = env,
            Labels = new Dictionary<string, string> { ["com.docker.compose.project"] = runtime.ComposeProject },
            HostConfig = new HostConfig { NetworkMode = runtime.ComposeNetwork },
        }, ct);

        // Everything from here on has a running (or about to run) k6 container on its hands - a
        // client aborting the request (tab closed, fetch aborted) cancels ct mid-await, or any of
        // these calls can just throw. Without cleanup on that path, the container is never stopped
        // or removed: it keeps generating its full --vus/--duration load against the stack after
        // the request that started it is gone, then lingers as clutter afterward. Every exit from
        // this point must go through the catch below.
        try
        {
            await using (var tarStream = await BuildScriptTarAsync(scriptPath, ct))
            {
                // The destination itself must already exist for the Docker API to accept the
                // extraction - "/" always does, and the tar entry's own "scripts/scenario.js" path
                // makes the extraction create that subdirectory as it unpacks.
                await runtime.Client.Containers.ExtractArchiveToContainerAsync(
                    created.ID,
                    new ContainerPathStatParameters { Path = "/" },
                    tarStream,
                    ct);
            }

            logger.LogWarning(
                "Running k6 scenario {Scenario} ({Vus} VUs{Mode}) as {ContainerId}",
                request.Scenario, request.Vus,
                targetIterations is { } t ? $", {t} iterations" : request.Stages is { Count: > 0 } s ? $", {totalSeconds}s, {s.Count} stages" : $", {totalSeconds}s",
                created.ID);

            await runtime.Client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);

            var output = await StreamLogsWithProgressAsync(created.ID, totalSeconds, targetIterations, onProgress, ct);

            var inspect = await runtime.Client.Containers.InspectContainerAsync(created.ID, ct);
            var report = await ReadSummaryAsync(created.ID, request.Scenario, stepIds, inspect.State.ExitCode, output, ct);
            await runtime.Client.Containers.RemoveContainerAsync(created.ID, new ContainerRemoveParameters(), ct);

            return report;
        }
        catch
        {
            await TryStopAndRemoveContainerAsync(created.ID);
            throw;
        }
    }

    // Best-effort cleanup for a k6 container abandoned by an exception or cancellation partway
    // through RunAsync. Uses CancellationToken.None deliberately - the caller's ct may itself be
    // the cancellation that got us here, and stopping/removing with an already-cancelled token
    // would just throw immediately, leaving the container running. Each step is independent and
    // swallows its own failure (already stopped/removed, or the daemon is unreachable) so a failed
    // stop doesn't skip the remove attempt.
    private async Task TryStopAndRemoveContainerAsync(string containerId)
    {
        try
        {
            await runtime.Client.Containers.StopContainerAsync(
                containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stop abandoned k6 container {ContainerId} during cleanup", containerId);
        }

        try
        {
            await runtime.Client.Containers.RemoveContainerAsync(
                containerId, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove abandoned k6 container {ContainerId} during cleanup", containerId);
        }
    }

    // Follows the container's combined stdout/stderr as it runs, parsing k6's own once-a-second
    // "running (Ns), X/X VUs, N complete..." status lines (via K6ProgressParser) into progress
    // pushes, while also building up the full text for the raw-output field of the final report.
    // Returns once the stream hits EOF, which happens when k6 itself exits. targetIterations
    // non-null means an iteration-count run - there's no fixed total time to measure against, so
    // percent comes from iterations done/target instead of elapsed/totalSeconds, and the "past the
    // requested duration" skip below doesn't apply (totalSeconds is 0 in that mode and never meant
    // to bound anything).
    // A chaos run (high loss/latency, many VUs, up to 600s) makes k6 print a WARN per failed
    // request - that can reach tens of MB of text, which then gets held in memory for the run's
    // whole duration, pushed whole over SignalR on completion, and written into the run's JSON
    // history file. 1MB is far more than anyone reads of a report by hand; progress parsing below
    // is unaffected since it works off pendingLine, not this capped copy.
    private const int MaxRawOutputChars = 1_000_000;
    private const string RawOutputTruncatedMarker = "\n... [output truncated, exceeded 1MB] ...\n";

    private async Task<string> StreamLogsWithProgressAsync(string containerId, int totalSeconds, int? targetIterations, Func<TrafficProgress, Task> onProgress, CancellationToken ct)
    {
        using var logStream = await runtime.Client.Containers.GetContainerLogsAsync(
            containerId,
            tty: false,
            new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = true },
            ct);

        var fullOutput = new StringBuilder();
        var outputTruncated = false;
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
            if (!outputTruncated)
            {
                outputTruncated = AppendCapped(fullOutput, chunk, MaxRawOutputChars);
            }
            pendingLine.Append(chunk);

            var lines = pendingLine.ToString().Split('\n');
            for (var i = 0; i < lines.Length - 1; i++)
            {
                var parsed = K6ProgressParser.TryParse(lines[i]);
                if (parsed is not { } match)
                {
                    continue;
                }

                var elapsed = match.ElapsedSeconds;
                if (targetIterations is null && elapsed > totalSeconds)
                {
                    // k6 prints one extra status line during its graceful-stop tail, past the
                    // requested duration - skip it rather than report a misleading rate spike from
                    // the tiny elapsed delta. Doesn't apply to iteration-count runs, which have no
                    // requested duration to be "past".
                    continue;
                }

                var activeVus = match.ActiveVus;
                var iterations = match.Iterations;
                var elapsedDelta = elapsed - lastElapsed;
                var rate = elapsedDelta > 0 ? (iterations - lastIterations) / elapsedDelta : 0;
                lastElapsed = elapsed;
                lastIterations = iterations;

                var percent = targetIterations is { } target
                    ? (int)Math.Min(100, (double)iterations / target * 100)
                    : (int)Math.Min(100, elapsed / totalSeconds * 100);
                await onProgress(new TrafficProgress((int)elapsed, totalSeconds, percent, activeVus, iterations, rate, targetIterations));
            }

            pendingLine.Clear();
            pendingLine.Append(lines[^1]);
        }

        return fullOutput.ToString();
    }

    // Appends chunk to builder unless/until maxChars is reached, at which point it appends a
    // truncation marker once and reports "already truncated" from then on - split out from
    // StreamLogsWithProgressAsync as a pure, static, easily unit-tested piece of an otherwise
    // heavily Docker-API-coupled method.
    internal static bool AppendCapped(StringBuilder builder, string chunk, int maxChars)
    {
        if (builder.Length >= maxChars)
        {
            return true;
        }

        // >= , not >: a chunk that lands exactly on the boundary must also take this branch. With
        // a plain >, that chunk fell through to the plain-append path below with the builder now
        // sitting exactly at maxChars but reported as "not truncated yet" - the *next* call would
        // then hit the first branch above (builder.Length >= maxChars) and return true immediately,
        // with the truncation marker never appended at all.
        if (builder.Length + chunk.Length >= maxChars)
        {
            builder.Append(chunk, 0, Math.Max(0, maxChars - builder.Length));
            builder.Append(RawOutputTruncatedMarker);
            return true;
        }

        builder.Append(chunk);
        return false;
    }

    private async Task<TrafficReport> ReadSummaryAsync(string containerId, string scenario, IReadOnlyList<string> stepIds, long exitCode, string rawOutput, CancellationToken ct)
    {
        try
        {
            var response = await runtime.Client.Containers.GetArchiveFromContainerAsync(
                containerId, new GetArchiveFromContainerParameters { Path = "/scripts/summary.json" }, false, ct);
            using var responseStream = response.Stream;

            // Buffer fully before handing to TarReader - it does non-sequential-looking reads via
            // SubReadStream over each entry's data, which doesn't play well with the raw chunked
            // HTTP stream the Docker API hands back (hit EndOfStreamException reading directly).
            using var buffered = new MemoryStream();
            await responseStream.CopyToAsync(buffered, ct);
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
                return K6SummaryParser.Parse(json, scenario, stepIds, exitCode, rawOutput);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read k6 summary.json for {Scenario} - falling back to raw output only", scenario);
        }

        return new TrafficReport(scenario, exitCode, 0, 0, 0, 0, 0, 0, 0, null, [], [], rawOutput);
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

            // TarEntry doesn't take ownership of/dispose DataStream - PaxTarEntry just holds the
            // reference and WriteEntryAsync reads from it synchronously within this call, so it's
            // safe (and necessary) to close the file handle immediately after, rather than leaking
            // one per traffic run started.
            using (var scriptStream = File.OpenRead(scriptPath))
            {
                var fileEntry = new PaxTarEntry(TarEntryType.RegularFile, "scripts/scenario.js")
                {
                    DataStream = scriptStream,
                    Mode = (UnixFileMode)0644,
                };
                await writer.WriteEntryAsync(fileEntry, ct);
            }
        }

        tarStream.Position = 0;
        return tarStream;
    }
}
