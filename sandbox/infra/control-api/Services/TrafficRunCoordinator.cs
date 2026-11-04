using ControlApi.Hubs;
using ControlApi.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

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
    ITrafficService traffic,
    IContainerLifecycleService lifecycle,
    IInfraToggleService infraToggle,
    IPgcatService pgcat,
    IPostgresService postgres,
    IRedisInfraService redis,
    IRunHistoryStore runHistory,
    IHubContext<StatusHub> hub,
    TraceStore traceStore,
    RunResourceMaxTracker resourceMaxTracker,
    IOptions<BottleneckThresholds> bottleneckThresholds,
    ILogger<TrafficRunCoordinator> logger)
{
    // Expresses "one run at a time" as a real slot instead of a hand-rolled bool+lock. TryStart
    // claims it with Wait(0) (never blocks - a rejected request just means someone else already
    // has it); exactly one of ReleaseIfOwner's two call sites (the early release in RunAsync, right
    // after k6 itself finishes, or the finally below as a safety net for the cancel/exception
    // paths) ever actually calls Release() for a given run - see ReleaseIfOwner's own comment for
    // why that's guaranteed by construction, not by luck. Replaced a previous _gate (Lock) +
    // _runActive (bool) + _runCts design - found in review: that version's ReleaseSlot() mutated
    // the fields directly rather than a value scoped to the specific run calling it, so a run's own
    // early release (followed by a new run claiming the slot) let that same run's later, second
    // ReleaseSlot() call (its finally-block safety net) dispose and tear down the NEW run's CTS
    // instead of a no-op - breaking the "one run at a time" invariant RunResourceMaxTracker depends
    // on. This design also drops the Lock entirely, closing a reentrancy concern the old version
    // had to work around (Cancel() deliberately called outside the lock, to avoid a token callback
    // re-entering it).
    private readonly SemaphoreSlim _runSlot = new(1, 1);

    // The active run's own CancellationTokenSource, or null when no run is active. Every write is
    // either TryStart claiming it (Volatile.Write - safe because only the thread that just won
    // _runSlot.Wait(0) ever writes a new value here) or ReleaseIfOwner clearing it
    // (Interlocked.CompareExchange - see that method). TryCancel only ever reads it.
    private CancellationTokenSource? _activeRunCts;

    // Returns false when another run already holds the slot. Callers validate the request first: the
    // slot is only claimed here, so a rejected request never blocks a real run from starting.
    public bool TryStart(TrafficRequest request)
    {
        if (!_runSlot.Wait(0))
        {
            return false;
        }

        var cts = new CancellationTokenSource();
        Volatile.Write(ref _activeRunCts, cts);

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(request, cts);
            }
            finally
            {
                // No-op on the success path (already released early inside RunAsync, right after
                // k6 itself finished) - this is the safety net for the cancellation/exception paths,
                // which never reach that early release.
                ReleaseIfOwner(cts);
            }
        });
        return true;
    }

    public bool TryCancel()
    {
        // Deliberately just a read, no ownership change - TryCancel never competes with
        // ReleaseIfOwner for the slot, it only ever asks whatever run is currently active (if any)
        // to cancel itself.
        var cts = Volatile.Read(ref _activeRunCts);
        if (cts is null)
        {
            return false;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run this cts belonged to already released the slot (ReleaseIfOwner disposed it)
            // in the window between the read above and Cancel() here - deliberately read outside
            // any lock, so this race is accepted rather than prevented: nothing left to cancel, same
            // outcome as _activeRunCts already being null.
            return false;
        }

        return true;
    }

    // Releases the slot for exactly one run, identified by its own cts - never whatever cts the
    // field currently happens to hold. Called (up to) twice per run: the early release in RunAsync
    // below, and TryStart's finally-block safety net. CompareExchange guarantees only the FIRST of
    // those two calls to actually run for a given cts ever swaps the field and releases the
    // semaphore: nothing else can change _activeRunCts away from this cts before then (the slot
    // this run holds isn't free yet), and by the time Release() below makes the slot available to a
    // brand new TryStart, the field has already been nulled - so that new run's own cts can never be
    // clobbered by this run's second (by then stale) call. The second call simply finds the field no
    // longer equal to its own cts and is a no-op.
    private void ReleaseIfOwner(CancellationTokenSource cts)
    {
        if (Interlocked.CompareExchange(ref _activeRunCts, null, cts) == cts)
        {
            cts.Dispose();
            _runSlot.Release();
        }
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

            var report = await traffic.RunTrafficAsync(
                request,
                progress => hub.Clients.All.SendAsync("trafficProgress", progress),
                cts.Token);

            var runEnd = DateTimeOffset.UtcNow;
            // Always ends tracking, even if the run failed/returned null - otherwise a failed run
            // would leave the tracker stuck "on" and silently accumulate maxima into whatever the
            // next run turns out to be.
            var resourceMaxima = resourceMaxTracker.EndRun();

            // Release the "only one run at a time" slot as soon as the run itself (the k6 container
            // and its live resource tracking) is actually finished, not after SaveSnapshotAsync below
            // also completes - that method has its own ~2s delay waiting for trace spans to flush
            // (see its own comment) and doesn't need the guard: resourceMaxima is already captured
            // above, and nothing else SaveSnapshotAsync touches depends on the slot. Without this,
            // a new run attempted during that delay gets rejected even though nothing is visibly
            // running anymore.
            ReleaseIfOwner(cts);

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
            var containers = await lifecycle.ListContainersAsync(CancellationToken.None);
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

            // The first replica only, as a representative sample - not all 3 instances. Capturing
            // and displaying 3 instances' worth of pool stats per run (RunSnapshot, RunHistoryPanel,
            // CompareRunsModal all expect one PgcatConnectionStats today) would be a much bigger
            // shape change than this run-metadata feature warrants; see BottleneckAdvisor's own
            // comment on the same simplification. Each instance's live pools are still inspectable
            // by hand via the pgcat node's own panel at any time (it shows all 3), same as before.
            var allPgcatConnections = await pgcat.GetAllPgcatConnectionsAsync(CancellationToken.None);
            var pgcatConnections = allPgcatConnections.Count > 0 ? allPgcatConnections[0].Stats : null;
            var traceHops = traceStore.GetHopStatsBetween(runStart, runEnd);
            var thresholds = bottleneckThresholds.Value;
            var verdict = BottleneckAdvisor.Analyze(report, resourceMaxima, traceHops, pgcatConnections, thresholds);
            // Looked up before this run is saved below, so it can never match itself.
            var baseline = await FindBaselineComparisonAsync(request.Scenario, report, CancellationToken.None);

            var snapshot = new RunSnapshot(
                $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}",
                DateTimeOffset.UtcNow,
                request,
                await infraToggle.GetInfraStatusAsync(CancellationToken.None),
                replicas,
                pgcatConnections,
                await postgres.GetPostgresConnectionsAsync(CancellationToken.None),
                report,
                pgcat.GetPgcatPoolSettings(),
                sentinel,
                replicationLags,
                infraToggle.GetRabbitMqPrefetch(),
                infraToggle.GetMongoReadPreference(),
                infraToggle.GetNpgsqlPoolSize(),
                resourceMaxima,
                traceHops,
                verdict,
                thresholds,
                baseline,
                infraToggle.GetL1CacheEnabled(),
                infraToggle.GetNegativeCacheEnabled());

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

    // "Baseline" means the most recent *prior* run of this exact scenario - a different scenario's
    // numbers (e.g. a resolve-only flow vs. a full register+create+click flow) would be meaningless
    // to compare against. ListAsync already returns runs sorted newest-first, and this run hasn't
    // been saved yet when this runs, so the first scenario match is guaranteed to be an earlier run,
    // never this one.
    private async Task<BaselineComparison?> FindBaselineComparisonAsync(string scenario, TrafficReport report, CancellationToken ct)
    {
        try
        {
            var pastRuns = await runHistory.ListAsync(ct);
            var baselineSummary = pastRuns.FirstOrDefault(r => r.Scenario == scenario);
            if (baselineSummary is null)
            {
                return null;
            }

            var baselineSnapshot = await runHistory.GetAsync(baselineSummary.Id, ct);
            return baselineSnapshot is null
                ? null
                : BaselineComparer.Compare(report, baselineSnapshot.Report, baselineSummary.Id, baselineSummary.Timestamp);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to compute baseline comparison for scenario {Scenario}", scenario);
            return null;
        }
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
