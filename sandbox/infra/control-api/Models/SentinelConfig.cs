namespace ControlApi.Models;

// The 3 Sentinel-tunable failover parameters, applied identically to all three redis-sentinel-N
// containers (each tracks its own local copy - SENTINEL SET on one doesn't propagate to the others,
// see DockerService.SetSentinelConfigAsync). Live SENTINEL SET/MASTER commands, no file edit or
// restart - see infra/redis/sentinel.conf for the shipped defaults these start from.
public record SentinelConfig(int DownAfterMs, int Quorum, int FailoverTimeoutMs);
