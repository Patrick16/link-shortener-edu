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
    int? NpgsqlPoolSize = null,
    // Not a field on Infra (InfraStatus) itself - see that record's own comment on why L1 caching
    // is a per-node capability, not a Topology-staged toggle. Captured/restored here the same
    // best-effort way as the three fields above.
    bool? L1CacheEnabled = null,
    // Same reasoning as L1CacheEnabled above.
    bool? NegativeCacheEnabled = null);
