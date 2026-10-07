using ControlApi.Models;

namespace ControlApi.Services;

public interface IRedisInfraService
{
    // Which physical container is actually master/primary right now, read directly off Redis's own
    // data-plane nodes rather than assumed from architecture.json's static labels - Sentinel can
    // re-elect a leader with zero involvement from this app. See NodeRole.
    Task<InfraTopology> GetRedisTopologyAsync(CancellationToken ct);

    // Redis Sentinel's own live SENTINEL SET/MASTER commands - applied to all 3 sentinel containers
    // at once (each tracks its own local config independently). See SentinelConfig.
    Task<SentinelConfig?> GetSentinelConfigAsync(CancellationToken ct);

    Task<SentinelConfig> SetSentinelConfigAsync(SentinelConfig config, CancellationToken ct);

    // Re-derives the real Redis master directly (ROLE asked of each of the three data containers)
    // and corrects any Sentinel found monitoring a different address - Sentinel's own remembered
    // address can go silently stale after a redis-master recreate it never observed as a failure.
    Task SelfHealSentinelAsync(CancellationToken ct);

    // Runs `redis-cli FLUSHALL` inside the redis container via Docker's exec API - lets a demo show
    // cold-cache behavior on demand. Returns the command's own output, or null if redis isn't
    // running.
    Task<string?> FlushRedisAsync(CancellationToken ct);
}
