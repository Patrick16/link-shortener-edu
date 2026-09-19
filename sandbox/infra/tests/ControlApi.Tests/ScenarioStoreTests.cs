using ControlApi.Models;
using ControlApi.Services;
using Microsoft.Extensions.Configuration;

namespace ControlApi.Tests;

public class ScenarioStoreTests : IDisposable
{
    private readonly string _tempDir;

    public ScenarioStoreTests()
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

    private ScenarioStore NewSut()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scenarios:DataDir"] = _tempDir })
            .Build();
        return new ScenarioStore(config);
    }

    private static CustomScenario NewScenario(string name = "smoke-test") => new(
        Name: name,
        Steps: [new FlowStep("create-link"), new FlowStep("resolve-link", PauseAfterSeconds: 1)],
        Mode: "duration",
        TotalDurationSeconds: 30,
        Points: [new ScenarioPoint(0, 1), new ScenarioPoint(30, 10)]);

    [Fact]
    public async Task ListAsync_NoFileYet_ReturnsEmpty()
    {
        var sut = NewSut();

        var scenarios = await sut.ListAsync(CancellationToken.None);

        Assert.Empty(scenarios);
    }

    [Fact]
    public async Task SaveAsync_ThenListAsync_ReturnsSavedScenario()
    {
        var sut = NewSut();
        var scenario = NewScenario();

        await sut.SaveAsync(scenario, CancellationToken.None);
        var scenarios = await sut.ListAsync(CancellationToken.None);

        // CustomScenario's generated equality compares Steps/Points by list reference, not
        // structurally, so a round-tripped (deserialized) instance is never == the original.
        var saved = Assert.Single(scenarios);
        Assert.Equal(scenario.Name, saved.Name);
        Assert.Equal(scenario.Mode, saved.Mode);
        Assert.Equal(scenario.TotalDurationSeconds, saved.TotalDurationSeconds);
        Assert.Equal(scenario.Steps, saved.Steps);
        Assert.Equal(scenario.Points, saved.Points);
    }

    [Fact]
    public async Task SaveAsync_SameNameTwice_ReplacesRatherThanDuplicates()
    {
        var sut = NewSut();
        await sut.SaveAsync(NewScenario() with { TotalDurationSeconds = 30 }, CancellationToken.None);

        await sut.SaveAsync(NewScenario() with { TotalDurationSeconds = 60 }, CancellationToken.None);

        var scenarios = await sut.ListAsync(CancellationToken.None);
        var saved = Assert.Single(scenarios);
        Assert.Equal(60, saved.TotalDurationSeconds);
    }

    [Fact]
    public async Task DeleteAsync_ExistingScenario_RemovesItAndReturnsTrue()
    {
        var sut = NewSut();
        await sut.SaveAsync(NewScenario("keep-me"), CancellationToken.None);
        await sut.SaveAsync(NewScenario("delete-me"), CancellationToken.None);

        var deleted = await sut.DeleteAsync("delete-me", CancellationToken.None);

        Assert.True(deleted);
        var remaining = await sut.ListAsync(CancellationToken.None);
        Assert.Equal(["keep-me"], remaining.Select(s => s.Name));
    }

    [Fact]
    public async Task DeleteAsync_UnknownScenario_ReturnsFalse()
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
        await new ScenarioStore(config).SaveAsync(NewScenario(), CancellationToken.None);

        var scenarios = await new ScenarioStore(config).ListAsync(CancellationToken.None);

        Assert.Single(scenarios);
    }
}
