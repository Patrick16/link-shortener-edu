namespace ControlApi.Models;

// The "desired config" half of RunSnapshot, named on its own so a Preset can reference it
// without dragging in the run-result fields (Request/Report/connections/trace/verdict) that
// don't apply to "reconfigure the stand to this," only to "here's what happened during a run."
public record InfraConfigSnapshot(
    InfraStatus Infra,
    IReadOnlyList<ReplicaCount> Replicas,
    PgcatPoolSettings? PgcatPool = null,
    SentinelConfig? Sentinel = null,
    IReadOnlyList<ReplicationLagEntry>? ReplicationLags = null,
    int? RabbitMqPrefetchCount = null,
    string? MongoReadPreference = null,
    int? NpgsqlPoolSize = null);
