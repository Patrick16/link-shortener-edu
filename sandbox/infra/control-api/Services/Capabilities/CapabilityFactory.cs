using System.Text.Json;

namespace ControlApi.Services.Capabilities;

// Builds the set of component-specific capability endpoint-registrars actually referenced by
// architecture.json, instead of Program.cs unconditionally mapping all of them - each frontend node
// there lists its own "capabilities" strings, and only the ones with a backend registration here
// (a "capability" string can also be frontend-only, e.g. "scalable", "pgcat-connections",
// "postgres-connections" - no entry for those below, silently skipped) get their routes mapped.
public sealed class CapabilityFactory
{
    private readonly Dictionary<string, Func<IDockerService, ILoggerFactory, IComponentCapability>> _factories = new()
    {
        ["pgcat-pool"] = (docker, loggerFactory) => new PgcatPoolCapability(docker, loggerFactory.CreateLogger<PgcatPoolCapability>()),
        ["npgsql-pool-size"] = (docker, loggerFactory) => new NpgsqlPoolSizeCapability(docker, loggerFactory.CreateLogger<NpgsqlPoolSizeCapability>()),
        ["pgcat-toggle"] = (docker, loggerFactory) => new PgcatToggleCapability(docker, loggerFactory.CreateLogger<PgcatToggleCapability>()),
        ["cache-toggle"] = (docker, loggerFactory) => new CacheToggleCapability(docker, loggerFactory.CreateLogger<CacheToggleCapability>()),
        ["nginx-toggle"] = (docker, _) => new NginxToggleCapability(docker),
        ["sentinel-config"] = (docker, _) => new SentinelConfigCapability(docker),
        ["rabbitmq-prefetch"] = (docker, loggerFactory) => new RabbitMqPrefetchCapability(docker, loggerFactory.CreateLogger<RabbitMqPrefetchCapability>()),
        ["mongo-read-preference"] = (docker, loggerFactory) => new MongoReadPreferenceCapability(docker, loggerFactory.CreateLogger<MongoReadPreferenceCapability>()),
        ["replication-lag"] = (docker, _) => new ReplicationLagCapability(docker),
        ["flush-cache"] = (docker, _) => new FlushCacheCapability(docker),
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public IReadOnlyList<IComponentCapability> BuildFromArchitectureFile(string path, IDockerService docker, ILoggerFactory loggerFactory)
    {
        var json = File.ReadAllText(path);
        var document = JsonSerializer.Deserialize<ArchitectureDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Architecture file {path} did not deserialize to a valid document");

        var capabilityNames = document.Components
            .SelectMany(c => c.Capabilities ?? [])
            .Distinct();

        var result = new List<IComponentCapability>();
        foreach (var name in capabilityNames)
        {
            if (_factories.TryGetValue(name, out var factory))
            {
                result.Add(factory(docker, loggerFactory));
            }
        }

        return result;
    }

    private sealed record ArchitectureDocument(List<ArchitectureComponentRef> Components);

    private sealed record ArchitectureComponentRef(List<string>? Capabilities);
}
