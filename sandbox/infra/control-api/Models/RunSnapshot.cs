namespace ControlApi.Models;

public record ReplicaCount(string ServiceId, int Count);

// Everything needed to answer "under what configuration did this result happen" later, not just
// the report itself - the same report can mean different things depending on whether pgcat/cache
// were on, how many replicas were running, and how loaded the DB connections already were.
public record RunSnapshot(
    string Id,
    DateTimeOffset Timestamp,
    TrafficRequest Request,
    InfraStatus Infra,
    IReadOnlyList<ReplicaCount> Replicas,
    PgcatConnectionStats? PgcatConnections,
    PostgresConnectionStats? PostgresConnections,
    TrafficReport Report);

// Lightweight row for the history list - avoids deserializing every run's full report (including
// its potentially large RawOutput) just to render a list of past runs.
public record RunSummary(string Id, DateTimeOffset Timestamp, string Scenario, long HttpRequests, long FailedRequests, long ExitCode);
