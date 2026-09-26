using ControlApi.Models;

namespace ControlApi.Services;

// Rule-based "where did this run's time/resources actually go" verdict - deliberately not
// ML/anomaly-detection, matching how every other experimental control in this app favors real,
// explainable mechanisms over a black box (see [[control-plane-design]]). Thresholds below are
// static and generous on purpose: this is a teaching tool running on a laptop-scale stack, not a
// production alerting system tuned against real traffic history - the point is to be right often
// enough to be a useful starting pointer, not to be precise.
//
// Analyze() and BuildChecklist() are two views over the exact same inputs so the auto-verdict and
// the "how would I have found this myself" guided panel can never disagree with each other.
public static class BottleneckAdvisor
{
    // NodeResourceMax.MaxCpuPercent comes straight from DockerService's own docker-stats-style
    // formula (cpuDelta/systemDelta * onlineCpus * 100 - see GetResourceSampleAsync), which is
    // normalized per CORE: 100% means one full core saturated, and a container can show hundreds
    // of percent on a many-core host without the host itself being anywhere near saturated -
    // confirmed live (rabbitmq showed 593% during a small run on a 32-core machine). Dividing by
    // the core count this control-api process itself sees turns it into "% of total host capacity"
    // before comparing against HighCpuPercent or showing it in evidence text - Environment.
    // ProcessorCount matches DockerService's own onlineCpus fallback reasoning (both assume the
    // container sees the host's full core set, true for this stack since nothing here sets a
    // per-container cpuset/cpu-quota limit).
    private static double NormalizedCpuPercent(double rawCpuPercent) => rawCpuPercent / Environment.ProcessorCount;

    private const double HighCpuPercent = 85;
    private const double HighMemoryPercent = 90;
    private const double SlowHopP95Ms = 300;
    private const double HighFailedRequestRate = 0.05;

    private static readonly IReadOnlyDictionary<string, string> NodeTypes = new Dictionary<string, string>
    {
        ["postgres"] = "postgres",
        ["postgres-replica1"] = "postgres",
        ["postgres-replica2"] = "postgres",
        ["pgcat"] = "pgcat",
        ["redis-master"] = "redis",
        ["redis-replica1"] = "redis",
        ["redis-replica2"] = "redis",
        ["rabbitmq"] = "rabbitmq",
        ["nginx"] = "nginx",
        ["mongo1"] = "mongo",
        ["mongo2"] = "mongo",
        ["mongo3"] = "mongo",
        ["auth-api"] = "service",
        ["link-api"] = "service",
        ["redirect-api"] = "service",
        ["shortener-service"] = "service",
        ["traffic-service"] = "service",
    };

    public static BottleneckVerdict Analyze(
        TrafficReport report,
        IReadOnlyList<NodeResourceMax> resourceMaxima,
        IReadOnlyList<TraceHopStats> traceHops,
        PgcatConnectionStats? pgcatConnections)
    {
        var suspects = new List<BottleneckSuspect>();

        // Rule 1: pgcat clients queued waiting for a free server connection - the clearest possible
        // "connection pool is the bottleneck" signal, since it means requests were already refused a
        // slot rather than merely running slowly.
        foreach (var pool in pgcatConnections?.Pools ?? [])
        {
            if (pool.ClientWaiting > 0)
            {
                suspects.Add(new BottleneckSuspect(
                    "pgcat",
                    "pgcat",
                    Severity: 100,
                    Evidence: $"{pool.ClientWaiting} client(s) were queued waiting on pool '{pool.Database}' (Client Waiting > 0)",
                    Recommendation: "The pgcat pool is exhausted - increase pool_size, enable/check read-write-splitting to replicas, or reduce concurrent load."));
            }
        }

        // Rule 2: resource saturation, any node - CPU and memory checked independently since a node
        // can be CPU-bound without being memory-bound and vice versa.
        foreach (var node in resourceMaxima)
        {
            var nodeType = NodeTypes.GetValueOrDefault(node.ServiceId, "other");
            var cpuPercentOfHost = NormalizedCpuPercent(node.MaxCpuPercent);

            if (cpuPercentOfHost >= HighCpuPercent)
            {
                suspects.Add(new BottleneckSuspect(
                    node.ServiceId,
                    nodeType,
                    Severity: 90,
                    Evidence: $"CPU reached {cpuPercentOfHost:F0}% of host capacity during the run",
                    Recommendation: RecommendationForCpu(nodeType, node.ServiceId)));
            }

            if (node.MaxMemoryPercent >= HighMemoryPercent)
            {
                suspects.Add(new BottleneckSuspect(
                    node.ServiceId,
                    nodeType,
                    Severity: 85,
                    Evidence: $"Memory reached {node.MaxMemoryPercent:F0}% of the container's limit",
                    Recommendation: "The container is close to OOM - raise its memory limit or reduce load/pool sizes on this node."));
            }
        }

        // Rule 3: any traced hop that's slow in absolute terms - a threshold, not just "the slowest
        // one", so a run where every hop is genuinely fast doesn't get a manufactured suspect.
        foreach (var hop in traceHops)
        {
            if (hop.P95Ms >= SlowHopP95Ms)
            {
                var nodeType = NodeTypes.GetValueOrDefault(hop.ServiceId, "other");
                suspects.Add(new BottleneckSuspect(
                    hop.ServiceId,
                    nodeType,
                    Severity: 70,
                    Evidence: $"'{hop.SpanName}' on {hop.ServiceId}: p95 {hop.P95Ms:F0} ms, max {hop.MaxMs:F0} ms ({hop.Count} calls)",
                    Recommendation: RecommendationForSlowHop(hop)));
            }
        }

        // Rule 4: failed requests, broken down by which status code dominates - a 502/503 wave
        // points at nginx/backend capacity, a 500 wave points at the backend itself misbehaving.
        if (report.HttpRequests > 0 && report.FailedRequestRate >= HighFailedRequestRate)
        {
            // DockerService's TrackedStatusCodes labels are human-readable ("502 Bad Gateway"), not
            // bare codes - match with StartsWith, the same way TrafficReportView.tsx's own
            // success/failure coloring already does, not an exact-string lookup against "502".
            var allStatusCounts = report.StatusBreakdownByEndpoint.SelectMany(e => e.StatusCounts).ToList();
            var badGateway = allStatusCounts.Where(s => s.Label.StartsWith("502") || s.Label.StartsWith("503")).Sum(s => s.Count);
            var serverError = allStatusCounts.Where(s => s.Label.StartsWith("500")).Sum(s => s.Count);

            if (badGateway > serverError)
            {
                suspects.Add(new BottleneckSuspect(
                    "nginx",
                    "nginx",
                    Severity: 80,
                    Evidence: $"{report.FailedRequestRate:P0} of requests failed, mostly 502/503 ({badGateway} of them)",
                    Recommendation: "The backend can't keep up with connections through nginx - scale up link-api/redirect-api replicas or check their health checks."));
            }
            else if (serverError > 0)
            {
                suspects.Add(new BottleneckSuspect(
                    "unknown-service",
                    "service",
                    Severity: 80,
                    Evidence: $"{report.FailedRequestRate:P0} of requests failed with 500 ({serverError} of them)",
                    Recommendation: "An application-side error, not overload - check the service's logs and the failed requests' traces in Aspire Dashboard."));
            }
        }

        var ranked = suspects.OrderByDescending(s => s.Severity).ToList();
        var checklist = BuildChecklist(resourceMaxima, traceHops, pgcatConnections, ranked);

        return new BottleneckVerdict(ranked, checklist);
    }

    // Same inputs as Analyze(), reframed as "what would you check, in what order, and what did you
    // actually find" - for the guided panel. Each step names a concrete finding when relevant data
    // exists, or an explicit "looks normal" instead of just staying silent - a clean signal is
    // still useful to see while learning what to look for.
    private static List<ChecklistStep> BuildChecklist(
        IReadOnlyList<NodeResourceMax> resourceMaxima,
        IReadOnlyList<TraceHopStats> traceHops,
        PgcatConnectionStats? pgcatConnections,
        IReadOnlyList<BottleneckSuspect> ranked)
    {
        var steps = new List<ChecklistStep>();

        var worstResource = resourceMaxima.OrderByDescending(n => n.MaxCpuPercent).FirstOrDefault();
        steps.Add(new ChecklistStep(
            "1. Resource maxima per node",
            "Open each node's panel on the graph and look at its CPU/memory peak during the run (sparklines + the numbers below). Note: the sparklines show CPU docker-stats style (100% = one core) - on a multi-core machine that's not the same as % of the whole host.",
            worstResource is null
                ? null
                : $"Most loaded: '{worstResource.ServiceId}' - CPU up to {NormalizedCpuPercent(worstResource.MaxCpuPercent):F0}% of host ({worstResource.MaxCpuPercent:F0}% docker-stats style), memory up to {worstResource.MaxMemoryPercent:F0}%."));

        var waitingPool = pgcatConnections?.Pools.FirstOrDefault(p => p.ClientWaiting > 0);
        steps.Add(new ChecklistStep(
            "2. Pool/queue saturation",
            "Check 'Client Waiting' on pgcat's panel (clients waiting for a free connection) and RabbitMQ's queue depth - a sign that a queue built up in front of a node, not that the node itself is slow.",
            waitingPool is null
                ? "No waiting clients found in pgcat's pools."
                : $"Pool '{waitingPool.Database}' has {waitingPool.ClientWaiting} client(s) waiting - the pgcat pool became a queue."));

        var slowestHop = traceHops.OrderByDescending(h => h.P95Ms).FirstOrDefault();
        steps.Add(new ChecklistStep(
            "3. Per-hop trace latency",
            "Compare p95/max across each hop (HTTP request, RabbitMQ publish/consume) - which one eats up the most time inside a single request.",
            slowestHop is null
                ? "No traces found for this window (check that otel-collector/control-api are up)."
                : $"Slowest hop: '{slowestHop.SpanName}' on {slowestHop.ServiceId} - p95 {slowestHop.P95Ms:F0} ms."));

        steps.Add(new ChecklistStep(
            "4. Full request picture in Aspire Dashboard",
            "Open Aspire Dashboard (http://localhost:18888/traces) and filter by the run's time window to see a real waterfall for one request - how much time went into HTTP, the queue, the DB.",
            null));

        if (ranked.Count > 0)
        {
            var top = ranked[0];
            steps.Add(new ChecklistStep(
                "5. Summary",
                "Compare your findings above with the automatic verdict below - they're built from the same data.",
                $"The verdict points at '{top.ServiceId}': {top.Evidence}"));
        }

        return steps;
    }

    private static string RecommendationForCpu(string nodeType, string serviceId) => nodeType switch
    {
        "postgres" => "The DB is CPU-bound - check indexes/heavy queries, consider sharding or reading from replicas.",
        "pgcat" => "The pooler itself became the CPU bottleneck - lower pool_size/overhead or give it its own dedicated resource.",
        "redis" => "Redis is CPU-bound - check the volume of operations (including eviction) and consider reading from replicas.",
        "rabbitmq" => "The broker is CPU-bound - check message size and the number of queues/consumers.",
        "nginx" => "nginx is CPU-bound - unlikely on this stack, but worth checking its worker_processes config.",
        "service" => $"'{serviceId}' is CPU-bound - scale out its replicas or profile its handler code.",
        _ => $"'{serviceId}' is CPU-bound - consider scaling or optimizing it.",
    };

    private static string RecommendationForSlowHop(TraceHopStats hop)
    {
        if (hop.SpanName.EndsWith(" consume", StringComparison.OrdinalIgnoreCase))
        {
            return $"Message processing on '{hop.ServiceId}' is slow - scale out worker replicas or raise PrefetchCount, or check whether the handler is DB-bound.";
        }

        if (hop.SpanName.EndsWith(" publish", StringComparison.OrdinalIgnoreCase))
        {
            return $"Publishing to RabbitMQ from '{hop.ServiceId}' is slow - check the broker's own health (CPU/disk) and network latency to it.";
        }

        return $"Request handling on '{hop.ServiceId}' is slow - look at the trace in Aspire Dashboard to see where inside the request the time actually goes (DB/cache/network).";
    }
}
