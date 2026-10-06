using ControlApi.Models;
using ControlApi.Services;
using Microsoft.Extensions.Configuration;

namespace ControlApi.Tests;

public class PresetStoreTests : IDisposable
{
    private readonly string _tempDir;

    public PresetStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "control-api-tests-" + Guid.NewGuid());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private PresetStore NewSut()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scenarios:DataDir"] = _tempDir })
            .Build();
        return new PresetStore(config);
    }

    private static Preset NewPreset(string name = "smoke-test") => new(
        Name: name,
        Config: new InfraConfigSnapshot(
            Infra: new InfraStatus(NginxBypassed: false, PgcatEnabled: true, CacheEnabled: true, MessagingMode: "rabbitmq"),
            Replicas: [new ReplicaCount("link-api", 1), new ReplicaCount("redirect-api", 1)]));

    [Fact]
    public async Task ListAsync_NoFileYet_ReturnsEmpty()
    {
        var sut = NewSut();

        var presets = await sut.ListAsync(CancellationToken.None);

        Assert.Empty(presets);
    }

    [Fact]
    public async Task SaveAsync_ThenListAsync_ReturnsSavedPreset()
    {
        var sut = NewSut();
        var preset = NewPreset();

        await sut.SaveAsync(preset, CancellationToken.None);
        var presets = await sut.ListAsync(CancellationToken.None);

        // Preset's generated equality compares Replicas by list reference, not structurally, so a
        // round-tripped (deserialized) instance is never == the original - same caveat as
        // ScenarioStoreTests' CustomScenario comparisons.
        var saved = Assert.Single(presets);
        Assert.Equal(preset.Name, saved.Name);
        Assert.Equal(preset.Config.Infra, saved.Config.Infra);
        Assert.Equal(preset.Config.Replicas, saved.Config.Replicas);
    }

    [Fact]
    public async Task SaveAsync_SameNameTwice_ReplacesRatherThanDuplicates()
    {
        var sut = NewSut();
        await sut.SaveAsync(NewPreset() with { ScenarioName = "first" }, CancellationToken.None);

        await sut.SaveAsync(NewPreset() with { ScenarioName = "second" }, CancellationToken.None);

        var presets = await sut.ListAsync(CancellationToken.None);
        var saved = Assert.Single(presets);
        Assert.Equal("second", saved.ScenarioName);
    }

    [Fact]
    public async Task DeleteAsync_ExistingPreset_RemovesItAndReturnsTrue()
    {
        var sut = NewSut();
        await sut.SaveAsync(NewPreset("keep-me"), CancellationToken.None);
        await sut.SaveAsync(NewPreset("delete-me"), CancellationToken.None);

        var deleted = await sut.DeleteAsync("delete-me", CancellationToken.None);

        Assert.True(deleted);
        var remaining = await sut.ListAsync(CancellationToken.None);
        Assert.Equal(["keep-me"], remaining.Select(p => p.Name));
    }

    [Fact]
    public async Task DeleteAsync_UnknownPreset_ReturnsFalse()
    {
        var sut = NewSut();

        var deleted = await sut.DeleteAsync("does-not-exist", CancellationToken.None);

        Assert.False(deleted);
    }

    [Fact]
    public async Task SaveAsync_PersistsAcrossNewStoreInstances()
    {
        // Simulates a control-api restart - the file, not the in-memory store, is the source of truth.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scenarios:DataDir"] = _tempDir })
            .Build();
        await new PresetStore(config).SaveAsync(NewPreset(), CancellationToken.None);

        var presets = await new PresetStore(config).ListAsync(CancellationToken.None);

        Assert.Single(presets);
    }
}
