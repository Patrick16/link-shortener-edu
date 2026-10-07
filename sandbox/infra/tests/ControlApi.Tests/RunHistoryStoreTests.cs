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

    private static readonly InfraStatus DefaultInfra = new(NginxBypassed: false, PgcatEnabled: true, CacheEnabled: true, MessagingMode: "rabbitmq");

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

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..%2F..%2Fscenarios%2Fsome-saved-scenario")]
    [InlineData("../custom-scenarios")]
    [InlineData("run.1")]
    [InlineData("/etc/passwd")]
    public async Task GetAsync_PathTraversalId_ReturnsNullWithoutTouchingTheFilesystem(string id)
    {
        // Regression: PathFor used to build the target path from the id with no validation at all -
        // Path.Combine happily resolves "../"-containing ids outside _dir, and control-api has no
        // authentication, so this was a real arbitrary-file-read (here) / arbitrary-file-delete
        // (DeleteAsync, below) primitive reachable from any local process.
        var sut = NewSut();

        var loaded = await sut.GetAsync(id, CancellationToken.None);

        Assert.Null(loaded);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..%2F..%2Fscenarios%2Fsome-saved-scenario")]
    [InlineData("../custom-scenarios")]
    [InlineData("run.1")]
    [InlineData("/etc/passwd")]
    public async Task DeleteAsync_PathTraversalId_ReturnsFalseWithoutDeletingAnything(string id)
    {
        var sut = NewSut();
        // A real file that a traversal id could plausibly reach for - ScenarioStore keeps
        // custom-scenarios.json directly in the same DataDir that RunHistoryStore's own "runs"
        // subdirectory lives under, so "../custom-scenarios.json" from inside "runs" would reach it.
        // Proves it's untouched, not just that the literal traversal path itself survives.
        var sentinelFile = Path.Combine(_tempDir, "custom-scenarios.json");
        await File.WriteAllTextAsync(sentinelFile, "[]");

        var deleted = await sut.DeleteAsync(id, CancellationToken.None);

        Assert.False(deleted);
        Assert.True(File.Exists(sentinelFile));
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
    public async Task ListAsync_SummaryIsCorrectEvenWithALargeRawOutputField()
    {
        // ReadSummaryAsync (what ListAsync now uses instead of a full RunSnapshot deserialization)
        // must still extract the right scalars even though it deliberately never reads RawOutput -
        // this is the direct correctness check for that change, not just a "does it still work" one.
        var sut = NewSut();
        var baseSnapshot = NewSnapshot("run-1");
        var snapshot = baseSnapshot with { Report = baseSnapshot.Report with { RawOutput = new string('x', 500_000) } };
        await sut.SaveAsync(snapshot, CancellationToken.None);

        var runs = await sut.ListAsync(CancellationToken.None);

        var summary = Assert.Single(runs);
        Assert.Equal("run-1", summary.Id);
        Assert.Equal(100, summary.HttpRequests);
        Assert.Equal(0, summary.FailedRequests);
        Assert.Equal(0, summary.ExitCode);
        Assert.Equal(10, summary.HttpRequestRate);
        Assert.Equal("smoke-test", summary.Scenario);
    }

    [Fact]
    public async Task GetAsync_ConcurrentWithClearAsync_NeverThrowsUnhandled()
    {
        // Regression: GetAsync used to check File.Exists then File.OpenRead with no lock at all,
        // racing SaveAsync's stale-file cleanup (and, once added, DeleteAsync/ClearAsync) - a delete
        // landing between those two calls threw FileNotFoundException instead of GetAsync returning
        // null. Now that GetAsync takes the same _lock as Save/Delete/Clear, running many of each
        // concurrently must never throw regardless of interleaving.
        var sut = NewSut();
        await sut.SaveAsync(NewSnapshot("run-1"), CancellationToken.None);

        var tasks = new List<Task>();
        for (var i = 0; i < 50; i++)
        {
            tasks.Add(sut.GetAsync("run-1", CancellationToken.None));
            tasks.Add(sut.ClearAsync(CancellationToken.None));
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task ClearAsync_OneFileLockedByAnotherHandle_DeletesTheRestAndReportsFailedCount()
    {
        // Regression: File.Delete inside the loop had no try/catch, unlike ListAsync's own per-file
        // catch for a corrupt file. A single delete throwing (permissions, or here, a file another
        // process/handle still has open) used to abort the whole loop, leaving every run after it
        // in iteration order still on disk while everything before it was already gone.
        //
        // Windows-only: the FileShare.Read handle below only blocks File.Delete under Windows'
        // mandatory share-mode locking. On Unix, unlink() doesn't check other open handles at all
        // (that's why you can delete a file a process still has open), so there's no portable way
        // to reproduce "locked by another handle" here - CI runs on ubuntu-latest, so skip there.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sut = NewSut();
        await sut.SaveAsync(NewSnapshot("run-locked"), CancellationToken.None);
        await sut.SaveAsync(NewSnapshot("run-a"), CancellationToken.None);
        await sut.SaveAsync(NewSnapshot("run-b"), CancellationToken.None);
        var lockedPath = Path.Combine(_tempDir, "runs", "run-locked.json");
        // FileShare.Read (not None): still lets ListAsync's own File.OpenRead succeed below - the
        // point is to block only the delete (Windows requires FileShare.Delete from every other open
        // handle before a file can be removed), not every other access to the file.
        using var lockHandle = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var failedCount = await sut.ClearAsync(CancellationToken.None);

        Assert.Equal(1, failedCount);
        var remaining = await sut.ListAsync(CancellationToken.None);
        Assert.Equal(["run-locked"], remaining.Select(r => r.Id));
    }

    [Fact]
    public async Task RenameAsync_KnownId_UpdatesScenarioOnBothSummaryAndFullSnapshot()
    {
        var sut = NewSut();
        await sut.SaveAsync(NewSnapshot("run-1"), CancellationToken.None);

        var summary = await sut.RenameAsync("run-1", "checkout spike", CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Equal("checkout spike", summary!.Scenario);
        var reloaded = await sut.GetAsync("run-1", CancellationToken.None);
        Assert.Equal("checkout spike", reloaded!.Request.Scenario);
        // Only Request.Scenario changes - the rest of the snapshot (including the k6 report's own
        // Scenario field) is left exactly as it was.
        Assert.Equal("smoke-test", reloaded.Report.Scenario);
    }

    [Fact]
    public async Task RenameAsync_UnknownId_ReturnsNull()
    {
        var sut = NewSut();

        var summary = await sut.RenameAsync("does-not-exist", "new-name", CancellationToken.None);

        Assert.Null(summary);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("../custom-scenarios")]
    [InlineData("run.1")]
    public async Task RenameAsync_PathTraversalId_ReturnsNullWithoutTouchingTheFilesystem(string id)
    {
        var sut = NewSut();

        var summary = await sut.RenameAsync(id, "new-name", CancellationToken.None);

        Assert.Null(summary);
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
