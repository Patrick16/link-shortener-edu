using ControlApi.Models;
using ControlApi.Services;
using Microsoft.Extensions.Configuration;

namespace ControlApi.Tests;

public class RunHistoryStoreTests : IDisposable
{
    private readonly string _tempDir;

    public RunHistoryStoreTests()
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

    private RunHistoryStore NewSut()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scenarios:DataDir"] = _tempDir })
            .Build();
        return new RunHistoryStore(config);
    }

    private static readonly InfraStatus DefaultInfra = new(NginxBypassed: false, PgcatEnabled: true, CacheEnabled: true);

    private static RunSnapshot NewSnapshot(string id, DateTimeOffset? timestamp = null) => new(
        Id: id,
        Timestamp: timestamp ?? DateTimeOffset.UtcNow,
        Request: new TrafficRequest("smoke-test", Vus: 5, DurationSeconds: 30, Steps: [new FlowStep("create-link")]),
        Infra: DefaultInfra,
        Replicas: [new ReplicaCount("link-api", 1)],
        PgcatConnections: null,
        PostgresConnections: null,
        Report: new TrafficReport(
            Scenario: "smoke-test",
            ExitCode: 0,
            HttpRequests: 100,
            HttpRequestRate: 10,
            FailedRequests: 0,
            FailedRequestRate: 0,
            Iterations: 100,
            IterationRate: 10,
            Vus: 5,
            HttpReqDuration: null,
            Checks: [],
            StatusBreakdownByEndpoint: [],
            RawOutput: ""));

    [Fact]
    public async Task ListAsync_NoRunsYet_ReturnsEmpty()
    {
        var sut = NewSut();

        var runs = await sut.ListAsync(CancellationToken.None);

        Assert.Empty(runs);
    }

    [Fact]
    public async Task SaveAsync_ThenGetAsync_ReturnsFullSnapshot()
    {
        var sut = NewSut();
        var snapshot = NewSnapshot("run-1");

        await sut.SaveAsync(snapshot, CancellationToken.None);
        var loaded = await sut.GetAsync("run-1", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.Id, loaded!.Id);
        Assert.Equal(snapshot.Report.HttpRequests, loaded.Report.HttpRequests);
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        var sut = NewSut();

        var loaded = await sut.GetAsync("does-not-exist", CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task ListAsync_ReturnsSummariesNewestFirst()
    {
        var sut = NewSut();
        var older = NewSnapshot("run-old", DateTimeOffset.UtcNow.AddMinutes(-10));
        var newer = NewSnapshot("run-new", DateTimeOffset.UtcNow);
        await sut.SaveAsync(older, CancellationToken.None);
        await sut.SaveAsync(newer, CancellationToken.None);

        var runs = await sut.ListAsync(CancellationToken.None);

        Assert.Equal(["run-new", "run-old"], runs.Select(r => r.Id));
    }

    [Fact]
    public async Task ListAsync_SummaryProjectsReportTotals()
    {
        var sut = NewSut();
        await sut.SaveAsync(NewSnapshot("run-1"), CancellationToken.None);

        var runs = await sut.ListAsync(CancellationToken.None);

        var summary = Assert.Single(runs);
        Assert.Equal(100, summary.HttpRequests);
        Assert.Equal(0, summary.FailedRequests);
        Assert.Equal("smoke-test", summary.Scenario);
    }

    [Fact]
    public async Task ListAsync_SkipsCorruptFileWithoutFailing()
    {
        var sut = NewSut();
        await sut.SaveAsync(NewSnapshot("run-good"), CancellationToken.None);
        var runsDir = Path.Combine(_tempDir, "runs");
        await File.WriteAllTextAsync(Path.Combine(runsDir, "run-corrupt.json"), "{ not valid json");

        var runs = await sut.ListAsync(CancellationToken.None);

        Assert.Equal(["run-good"], runs.Select(r => r.Id));
    }

    [Fact]
    public async Task SaveAsync_MoreThanTwoHundredRuns_PrunesOldestBeyondRetentionLimit()
    {
        var sut = NewSut();
        // Ids are timestamp-prefixed in production so a filename sort is chronological - zero-padded
        // sequence numbers reproduce that ordering here without needing real distinct timestamps.
        for (var i = 0; i < 205; i++)
        {
            await sut.SaveAsync(NewSnapshot($"run-{i:D5}"), CancellationToken.None);
        }

        var runs = await sut.ListAsync(CancellationToken.None);

        Assert.Equal(200, runs.Count);
        Assert.DoesNotContain(runs, r => r.Id is "run-00000" or "run-00001" or "run-00002" or "run-00003" or "run-00004");
        Assert.Contains(runs, r => r.Id == "run-00204");
    }
}
