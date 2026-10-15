using ControlApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlApi.Tests;

public class InfraToggleServiceCurrentStandingEnvTests
{
    private static InfraToggleService NewService()
    {
        var configuration = new ConfigurationBuilder().Build();
        var runtime = new ContainerRuntime(configuration, NullLogger<ContainerRuntime>.Instance);
        var pgcat = new PgcatService(configuration, runtime, NullLogger<PgcatService>.Instance);
        var redis = new RedisInfraService(runtime, NullLogger<RedisInfraService>.Instance);
        var mongo = new MongoTopologyService(runtime, NullLogger<MongoTopologyService>.Instance);
        return new InfraToggleService(runtime, pgcat, redis, mongo, NullLogger<InfraToggleService>.Instance);
    }

    [Fact]
    public void CurrentStandingEnv_DefaultState_MatchesDockerComposeYmlDefaults()
    {
        // This is the merge every toggle handler (SetPgcatEnabledAsync, SetCacheEnabledAsync,
        // SetNpgsqlPoolSizeAsync, SetRabbitMqPrefetchAsync, SetMongoReadPreferenceAsync) now folds
        // its own new value into before recreating a shared container - without it, recreating for
        // one axis silently reverts every other axis on that container to whatever
        // docker-compose.yml's own ${VAR:-default} falls back to. The keys/defaults here must stay
        // in sync with those fallback defaults; neither InfraToggleService's nor ContainerRuntime's
        // constructor touches the Docker daemon (the client connects lazily), so building an
        // instance here has no I/O.
        var sut = NewService();

        var env = sut.CurrentStandingEnv();

        Assert.Equal("haproxy", env["DB_HOST"]);
        Assert.Equal("6432", env["DB_PORT"]);
        Assert.Equal("true", env["CACHE_ENABLED"]);
        Assert.Equal("100", env["NPGSQL_MAX_POOL_SIZE"]);
        Assert.Equal("10", env["RABBITMQ_PREFETCH"]);
        Assert.Equal("primary", env["MONGO_READ_PREFERENCE"]);
        Assert.Equal("redis-sentinel-1:26379,redis-sentinel-2:26379,redis-sentinel-3:26379,serviceName=mymaster", env["REDIS_CONN"]);
        Assert.Equal("mongo1:27017,mongo2:27017,mongo3:27017", env["MONGO_HOSTS"]);
    }
}
