using System.Text.Json;

namespace ControlApi.Services.Capabilities;

// Builds the set of component-specific capability endpoint-registrars actually referenced by
// architecture.json, instead of Program.cs unconditionally mapping all of them - each frontend node
// there lists its own "capabilities" strings, and only the ones with a backend registration here
// (a "capability" string can also be frontend-only, e.g. "scalable", "pgcat-connections",
// "postgres-connections" - no entry for those below, silently skipped) get their routes mapped.
// Each factory resolves its own capability's specific service dependency from the DI container
// (IServiceProvider) rather than every capability sharing one god-interface - each depends on
// exactly the focused service its own domain needs (IPgcatService, IRedisInfraService,
// IInfraToggleService, ...), not on every unrelated one bundled together.
public sealed class CapabilityFactory
{
    private readonly Dictionary<string, Func<IServiceProvider, IComponentCapability>> _factories = new()
    {
        ["pgcat-pool"] = sp => new PgcatPoolCapability(sp.GetRequiredService<IPgcatService>(), sp.GetRequiredService<ILogger<PgcatPoolCapability>>()),
        ["npgsql-pool-size"] = sp => new NpgsqlPoolSizeCapability(sp.GetRequiredService<IInfraToggleService>(), sp.GetRequiredService<ILogger<NpgsqlPoolSizeCapability>>()),
        ["pgcat-toggle"] = sp => new PgcatToggleCapability(sp.GetRequiredService<IInfraToggleService>(), sp.GetRequiredService<ILogger<PgcatToggleCapability>>()),
        ["cache-toggle"] = sp => new CacheToggleCapability(sp.GetRequiredService<IInfraToggleService>(), sp.GetRequiredService<ILogger<CacheToggleCapability>>()),
        ["messaging-mode"] = sp => new MessagingToggleCapability(sp.GetRequiredService<IInfraToggleService>(), sp.GetRequiredService<ILogger<MessagingToggleCapability>>()),
        ["nginx-toggle"] = sp => new NginxToggleCapability(sp.GetRequiredService<IInfraToggleService>()),
        ["sentinel-config"] = sp => new SentinelConfigCapability(sp.GetRequiredService<IRedisInfraService>(), sp.GetRequiredService<ILogger<SentinelConfigCapability>>()),
        ["rabbitmq-prefetch"] = sp => new RabbitMqPrefetchCapability(sp.GetRequiredService<IInfraToggleService>(), sp.GetRequiredService<ILogger<RabbitMqPrefetchCapability>>()),
        ["mongo-read-preference"] = sp => new MongoReadPreferenceCapability(sp.GetRequiredService<IInfraToggleService>(), sp.GetRequiredService<ILogger<MongoReadPreferenceCapability>>()),
        ["replication-lag"] = sp => new ReplicationLagCapability(sp.GetRequiredService<IPostgresService>(), sp.GetRequiredService<ILogger<ReplicationLagCapability>>()),
        ["flush-cache"] = sp => new FlushCacheCapability(sp.GetRequiredService<IRedisInfraService>()),
        ["dlq-stats"] = sp => new DlqStatsCapability(sp.GetRequiredService<IRabbitMqService>(), sp.GetRequiredService<ILogger<DlqStatsCapability>>()),
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public IReadOnlyList<IComponentCapability> BuildFromArchitectureFile(string path, IServiceProvider services)
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
                result.Add(factory(services));
            }
        }

        return result;
    }

    private sealed record ArchitectureDocument(List<ArchitectureComponentRef> Components);

    private sealed record ArchitectureComponentRef(List<string>? Capabilities);
}
