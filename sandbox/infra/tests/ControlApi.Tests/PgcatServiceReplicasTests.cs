using ControlApi.Models;
using ControlApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlApi.Tests;

public class PgcatServiceReplicasTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private PgcatService NewService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Pgcat:ConfigDir"] = _tempDir })
            .Build();
        var runtime = new ContainerRuntime(configuration, NullLogger<ContainerRuntime>.Instance);
        return new PgcatService(configuration, runtime, NullLogger<PgcatService>.Instance);
    }

    private string ReadWrittenToml() => File.ReadAllText(Path.Combine(_tempDir, "pgcat.toml"));

    [Fact]
    public async Task SetReplicasEnabledAsync_False_DropsBothReplicasFromEveryPoolsServersList()
    {
        var sut = NewService();

        await sut.SetReplicasEnabledAsync(false, CancellationToken.None);

        var toml = ReadWrittenToml();
        Assert.DoesNotContain("postgres-replica1", toml);
        Assert.DoesNotContain("postgres-replica2", toml);
        // Still a valid single-server shard, not an accidentally-empty servers list.
        Assert.Contains("servers = [[\"postgres\", 5432, \"primary\"]]", toml);
    }

    [Fact]
    public async Task SetReplicasEnabledAsync_TrueAfterFalse_RestoresBothReplicas()
    {
        var sut = NewService();

        await sut.SetReplicasEnabledAsync(false, CancellationToken.None);
        await sut.SetReplicasEnabledAsync(true, CancellationToken.None);

        var toml = ReadWrittenToml();
        Assert.Contains("postgres-replica1", toml);
        Assert.Contains("postgres-replica2", toml);
    }

    [Fact]
    public async Task SetReplicasEnabledAsync_False_DoesNotChangeCurrentPoolSettings()
    {
        var sut = NewService();
        await sut.SetPgcatPoolSettingsAsync(new PgcatPoolSettings("session", false, 7), CancellationToken.None);

        await sut.SetReplicasEnabledAsync(false, CancellationToken.None);

        // SetReplicasEnabledAsync only ever touches the servers list - pool_mode/read-write-splitting/
        // pool_size set just before it must survive untouched, both in the getter and in the file.
        Assert.Equal(new PgcatPoolSettings("session", false, 7), sut.GetPgcatPoolSettings());
        var toml = ReadWrittenToml();
        Assert.Contains("pool_mode = \"session\"", toml);
        Assert.Contains("pool_size = 7", toml);
    }

    [Fact]
    public async Task SetPgcatPoolSettingsAsync_AfterReplicasDisabled_KeepsReplicasExcluded()
    {
        var sut = NewService();
        await sut.SetReplicasEnabledAsync(false, CancellationToken.None);

        // Changing pool settings alone must not silently re-add the replicas this toggle excluded.
        await sut.SetPgcatPoolSettingsAsync(new PgcatPoolSettings("transaction", true, 30), CancellationToken.None);

        var toml = ReadWrittenToml();
        Assert.DoesNotContain("postgres-replica1", toml);
        Assert.DoesNotContain("postgres-replica2", toml);
    }
}
