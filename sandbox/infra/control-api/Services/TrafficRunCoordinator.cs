using ControlApi.Hubs;
using ControlApi.Models;
using Microsoft.AspNetCore.SignalR;

namespace ControlApi.Services;

// Owns the lifecycle of the (single) in-flight k6 traffic run: starts it in the background, reports
// over SignalR (trafficProgress while it runs, trafficCompleted with the final TrafficReport) instead
// of blocking the HTTP request for the full duration - lets the UI show a live progress bar instead
// of a frozen spinner - saves the run's snapshot, and lets it be cancelled.
//
// Singleton: the "only one run at a time" state lives here. Nothing enforced that before
// RunResourceMaxTracker existed either, but that tracker assumes one run at a time (see its own
// comment) - a second concurrent run would silently corrupt both runs' resource maxima (e.g. run 2's
// BeginRun() clearing state run 1 is still accumulating into).
public sealed class TrafficRunCoordinator(
    IDockerService docker,
    IPgcatService pgcat,
    IPostgresService postgres,
    IRedisInfraService redis,
    IRunHistoryStore runHistory,
    IHubContext<StatusHub> hub,
    TraceStore traceStore,
    RunResourceMaxTracker resourceMaxTracker,
    ILogger<TrafficRunCoordinator> logger)
{
    // 0 = idle, 1 = running.
    private int _runActive;

    // Set right before the run's Task.Run starts and cleared in its finally, guarded the same way as
    // _runActive (only one run - and so only one writer - at a time). Volatile.Read/Write rather than
    // a plain field so TryCancel, called from a different request's thread, is guaranteed to see the
    // latest value instead of a stale cached one.
    private CancellationTokenSource? _runCts;

    // Returns false when another run already holds the slot. Callers validate the request first: the
    // slot is only claimed here, so a rejected request never blocks a real run from starting.
    public bool TryStart(TrafficRequest request)
    {
        if (Interlocked.CompareExchange(ref _runActive, 1, 0) != 0)
        {
            return false;
        }

        var cts = new CancellationTokenSource();
        Volatile.Write(ref _runCts, cts);
        _ = Task.Run(() => RunAsync(request, cts));
        return true;
    }

    // Read-then-Cancel is fine without a lock: at most one run (and so one CancellationTokenSource)
    // is ever active at a time, and the reference is only ever nulled out after the run has already
    // stopped reading from it, so a cancel that lands after that just sees null.
    public bool TryCancel()
    {
        var activeCts = Volatile.Read(ref _runCts);
        if (activeCts is null)
        {
            return false;
        }

        activeCts.Cancel();
        return true;
    }

    private async Task RunAsync(TrafficRequest request, CancellationTokenSource cts)
    {
        try
        {
            // Window used to correlate trace spans and resource-usage peaks with this exact run -
            // captured around the k6 call itself, not the whole HTTP request handler, so it doesn't
            // pick up anything from validation or a previous run's tail.
            var runStart = DateTimeOffset.UtcNow;
            resourceMaxTracker.BeginRun();

            var report = await docker.RunTrafficAsync(
                request,
                progress => hub.Clients.All.SendAsync("trafficProgress", progress),
                cts.Token);

            var runEnd = DateTimeOffset.UtcNow;
            // Always ends tracking, even if the run failed/returned null - otherwise a failed run
            // would leave the tracker stuck "on" and silently accumulate maxima into whatever the
            // next run turns out to be.
            var resourceMaxima = resourceMaxTracker.EndRun();

            if (report is not null)
            {
                await hub.Clients.All.SendAsync("trafficCompleted", report);
                await SaveSnapshotAsync(request, report, resourceMaxima, runStart, runEnd);
            }
        }
        catch (OperationCanceledException)
        {
            // User-requested via /api/traffic/cancel - RunTrafficAsync's own catch already stopped
            // and removed the k6 container before this exception reached here, so there's nothing
            // left to clean up, just the UI to tell.
            logger.LogInformation("Traffic run for {Scenario} was cancelled", request.Scenario);
            await hub.Clients.All.SendAsync("trafficCancelled", new { request.Scenario });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Traffic run for {Scenario} failed", request.Scenario);
            await hub.Clients.All.SendAsync("trafficFailed", new { request.Scenario, error = ex.Message });
        }
        finally
        {
            Volatile.Write(ref _runCts, null);
            Volatile.Write(ref _runActive, 0);
        }
    }

    // Best-effort - a snapshot failing to save shouldn't hide the report the user is already looking
    // at. Captured right after the run so replica counts/connections reflect the state the load was
    // actually generated against, not some later moment.
    private async Task SaveSnapshotAsync(
        TrafficRequest request,
        TrafficReport report,
        IReadOnlyList<NodeResourceMax> resourceMaxima,
        DateTimeOffset runStart,
        DateTimeOffset runEnd)
    {
        try
        {
            var containers = await docker.ListContainersAsync(CancellationToken.None);
            var replicas = containers
                .GroupBy(c => c.ServiceId)
                .Select(g => new ReplicaCount(g.Key, g.Count(c => c.State == "running")))
                .ToList();

            var replicationLags = await ReadReplicationLagsAsync();
            var sentinel = await ReadSentinelConfigAsync();

            // Even with OTEL_BSP_SCHEDULE_DELAY shortened to 1s (see docker-compose.yml), spans from
            // the tail of the run are still exported on a timer, not the instant they finish -
            // querying the trace window immediately would systematically miss recent spans. A short
            // fixed wait is simpler and more honest than pretending to poll for "enough" spans
            // (there's no way to know the expected count in advance).
            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);

            var pgcatConnections = await pgcat.GetPgcatConnectionsAsync(CancellationToken.None);
            var traceHops = traceStore.GetHopStatsBetween(runStart, runEnd);
            var verdict = BottleneckAdvisor.Analyze(report, resourceMaxima, traceHops, pgcatConnections);

            var snapshot = new RunSnapshot(
                $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}",
                DateTimeOffset.UtcNow,
                request,
                docker.GetInfraStatus(),
                replicas,
                pgcatConnections,
                await postgres.GetPostgresConnectionsAsync(CancellationToken.None),
                report,
                pgcat.GetPgcatPoolSettings(),
                sentinel,
                replicationLags,
                docker.GetRabbitMqPrefetch(),
                docker.GetMongoReadPreference(),
                docker.GetNpgsqlPoolSize(),
                resourceMaxima,
                traceHops,
                verdict);

            await runHistory.SaveAsync(snapshot, CancellationToken.None);
            await hub.Clients.All.SendAsync("runSaved", new { snapshot.Id });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save run history snapshot for {Scenario}", request.Scenario);
        }
    }

    // Best-effort, same reasoning as the snapshot as a whole - none of the live reads below should
    // hide the report if a given control's read fails for some reason.
    private async Task<List<ReplicationLagEntry>> ReadReplicationLagsAsync()
    {
        var replicationLags = new List<ReplicationLagEntry>();
        foreach (var replicaId in new[] { "postgres-replica1", "postgres-replica2" })
        {
            try
            {
                var lag = await postgres.GetReplicationLagAsync(replicaId, CancellationToken.None);
                if (lag is not null)
                {
                    replicationLags.Add(new ReplicationLagEntry(replicaId, lag.DelayMs));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read replication lag for {ServiceId} while saving run snapshot", replicaId);
            }
        }

        return replicationLags;
    }

    private async Task<SentinelConfig?> ReadSentinelConfigAsync()
    {
        try
        {
            return await redis.GetSentinelConfigAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read Sentinel config while saving run snapshot");
            return null;
        }
    }
}
