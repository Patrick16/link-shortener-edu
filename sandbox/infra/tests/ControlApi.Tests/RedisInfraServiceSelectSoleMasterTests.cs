using ControlApi.Services;

namespace ControlApi.Tests;

public class RedisInfraServiceSelectSoleMasterTests
{
    private static RedisInfraService.RedisRoleObservation Observation(string serviceId, string role) => new(serviceId, role);

    [Fact]
    public void SelectSoleMaster_ExactlyOneMaster_ReturnsIt()
    {
        var observations = new[]
        {
            Observation("redis-master", "replica"),
            Observation("redis-replica1", "master"),
            Observation("redis-replica2", "replica"),
        };

        // The exact scenario SelfHealSentinelAsync exists for: a real failover already promoted
        // redis-replica1, and this must resolve to the node ROLE actually agrees is master right
        // now - not to whichever container happens to be named "redis-master".
        Assert.Equal("redis-replica1", RedisInfraService.SelectSoleMaster(observations));
    }

    [Fact]
    public void SelectSoleMaster_NoMasterReachable_ReturnsNull()
    {
        var observations = new[]
        {
            Observation("redis-master", "unreachable"),
            Observation("redis-replica1", "unreachable"),
            Observation("redis-replica2", "replica"),
        };

        Assert.Null(RedisInfraService.SelectSoleMaster(observations));
    }

    [Fact]
    public void SelectSoleMaster_TwoSimultaneousMasters_ReturnsNull()
    {
        // A real failover still mid-flight (the old master hasn't stepped down yet) or a genuine
        // split-brain both look like this for one tick - guessing which one is "right" here could
        // actively make a split-brain worse, so this must refuse to pick either.
        var observations = new[]
        {
            Observation("redis-master", "master"),
            Observation("redis-replica1", "master"),
            Observation("redis-replica2", "replica"),
        };

        Assert.Null(RedisInfraService.SelectSoleMaster(observations));
    }

    [Fact]
    public void SelectSoleMaster_NoObservationsAtAll_ReturnsNull()
    {
        Assert.Null(RedisInfraService.SelectSoleMaster([]));
    }
}
