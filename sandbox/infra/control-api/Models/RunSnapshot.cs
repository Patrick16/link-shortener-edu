namespace ControlApi.Models;

public record ReplicaCount(string ServiceId, int Count);

// One postgres replica's artificial WAL-replay delay at run time - Replicas above covers
// scale/count, this covers the ReplicationLag dial on top of it.
public record ReplicationLagEntry(string ServiceId, int DelayMs);

// Everything needed to answer "under what configuration did this result happen" later, not just
// the report itself - the same report can mean different things depending on whether pgcat/cache
// were on, how many replicas were running, and how loaded the DB connections already were. The
// trailing experimental-controls fields are nullable/optional so snapshots saved before they
// existed still deserialize (missing JSON properties just default to null).
public record RunSnapshot(
    string Id,
    DateTimeOffset Timestamp,
    TrafficRequest Request,
    InfraStatus Infra,
    IReadOnlyList<ReplicaCount> Replicas,
    PgcatConnectionStats? PgcatConnections,
    PostgresConnectionStats? PostgresConnections,
    TrafficReport Report,
    PgcatPoolSettings? PgcatPool = null,
    SentinelConfig? Sentinel = null,
    IReadOnlyList<ReplicationLagEntry>? ReplicationLags = null,
    int? RabbitMqPrefetchCount = null,
    string? MongoReadPreference = null,
    int? NpgsqlPoolSize = null,
    IReadOnlyList<NodeResourceMax>? ResourceMaxima = null,
    IReadOnlyList<TraceHopStats>? TraceHops = null,
    BottleneckVerdict? Verdict = null);

// Lightweight row for the history list - avoids deserializing every run's full report (including
// its potentially large RawOutput) just to render a list of past runs.
public record RunSummary(string Id, DateTimeOffset Timestamp, string Scenario, long HttpRequests, long FailedRequests, long ExitCode, double HttpRequestRate);
