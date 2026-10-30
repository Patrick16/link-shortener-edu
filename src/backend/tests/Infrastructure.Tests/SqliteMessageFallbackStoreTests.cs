namespace Infrastructure.Tests;

public class SqliteMessageFallbackStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"fallback-tests-{Guid.NewGuid()}.db");

    // Pooling=False: Microsoft.Data.Sqlite pools connections by default, which keeps the file
    // handle open even after each SqliteConnection is disposed - the temp file below could then
    // never be deleted in Dispose().
    private SqliteMessageFallbackStore NewSut() => new($"Data Source={_dbPath};Pooling=False");

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task GetPendingAsync_NoMessages_ReturnsEmpty()
    {
        var sut = NewSut();

        var pending = await sut.GetPendingAsync(CancellationToken.None);

        Assert.Empty(pending);
    }

    [Fact]
    public async Task SaveAsync_ThenGetPendingAsync_ReturnsSavedMessage()
    {
        var sut = NewSut();
        var message = new FallbackMessage("msg-1", "some.topic", """{"a":1}""", DateTime.UtcNow);

        await sut.SaveAsync(message, CancellationToken.None);
        var pending = await sut.GetPendingAsync(CancellationToken.None);

        var stored = Assert.Single(pending);
        Assert.Equal(message.MessageId, stored.MessageId);
        Assert.Equal(message.Topic, stored.Topic);
        Assert.Equal(message.Payload, stored.Payload);
    }

    [Fact]
    public async Task GetPendingAsync_ReturnsOldestFirst()
    {
        var sut = NewSut();
        var older = new FallbackMessage("old", "topic", "{}", DateTime.UtcNow.AddMinutes(-5));
        var newer = new FallbackMessage("new", "topic", "{}", DateTime.UtcNow);
        await sut.SaveAsync(newer, CancellationToken.None);
        await sut.SaveAsync(older, CancellationToken.None);

        var pending = await sut.GetPendingAsync(CancellationToken.None);

        Assert.Equal(["old", "new"], pending.Select(m => m.MessageId));
    }

    [Fact]
    public async Task DeleteAsync_RemovesMessage()
    {
        var sut = NewSut();
        await sut.SaveAsync(new FallbackMessage("msg-1", "topic", "{}", DateTime.UtcNow), CancellationToken.None);

        await sut.DeleteAsync("msg-1", CancellationToken.None);

        Assert.Empty(await sut.GetPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_DoesNotThrow()
    {
        var sut = NewSut();

        var exception = await Record.ExceptionAsync(() => sut.DeleteAsync("never-saved", CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task GetPendingAsync_CalledAgainImmediately_DoesNotReturnAlreadyClaimedRows()
    {
        // The direct regression this store's own race fix targets: with link-api/redirect-api
        // scaled to N replicas, every replica's RabbitMqRetryWorker points at the same shared file -
        // two replicas' ticks used to both get the same pending row before either deleted it, both
        // republish it, and the event gets processed twice downstream (RabbitMqConsumer has no
        // message-id dedup). Simulates that by calling GetPendingAsync twice in a row against the
        // same still-unresolved row, standing in for two concurrent replicas' ticks.
        var sut = NewSut();
        await sut.SaveAsync(new FallbackMessage("msg-1", "topic", "{}", DateTime.UtcNow), CancellationToken.None);

        var firstClaim = await sut.GetPendingAsync(CancellationToken.None);
        var secondClaim = await sut.GetPendingAsync(CancellationToken.None);

        Assert.Equal(["msg-1"], firstClaim.Select(m => m.MessageId));
        Assert.Empty(secondClaim);
    }

    [Fact]
    public async Task GetPendingAsync_ClaimIsStale_BecomesClaimableAgain()
    {
        // A row whose claim is stale (its replica crashed mid-tick, or a republish call itself hung
        // past the next tick) must eventually recover on its own, since nothing ever explicitly
        // "unclaims" a row on failure - it just stays claimed until the stale threshold passes.
        var sut = NewSut();
        await sut.SaveAsync(new FallbackMessage("msg-1", "topic", "{}", DateTime.UtcNow), CancellationToken.None);
        await sut.GetPendingAsync(CancellationToken.None); // claims it

        // Directly backdate the claim past the 2-minute stale threshold rather than waiting for it.
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE FailedMessages SET ClaimedAt = $stale WHERE MessageId = 'msg-1';";
        command.Parameters.AddWithValue("$stale", DateTime.UtcNow.AddMinutes(-10).ToString("O"));
        await command.ExecuteNonQueryAsync();

        var reclaimed = await sut.GetPendingAsync(CancellationToken.None);

        Assert.Equal(["msg-1"], reclaimed.Select(m => m.MessageId));
    }

    [Fact]
    public async Task SaveAsync_FileLockedByAnotherConnection_WaitsForBusyTimeoutInsteadOfThrowingImmediately()
    {
        // Found in review: with no busy timeout set, Microsoft.Data.Sqlite fails SQLITE_BUSY
        // immediately on any write contention instead of waiting - under N replicas sharing this
        // one file (see StaleClaimThreshold's own comment), that could lose an event on nothing
        // worse than two concurrent SaveAsync calls landing within the same few milliseconds, no
        // actual outage needed. Holds a real write lock open on a second connection to the same
        // file (an uncommitted INSERT inside a transaction) and releases it from a background task
        // shortly after - SaveAsync on the SUT must block until that release and then succeed,
        // rather than throwing right away.
        //
        // Both SaveAsync and the lock release run via Task.Run, deliberately not just awaited inline
        // one after another: Microsoft.Data.Sqlite's busy-handler retry blocks its calling thread
        // synchronously rather than yielding cooperatively (confirmed by this test initially
        // asserting Task.IsCompleted shortly after an unawaited call - it was already true, because
        // the whole busy-wait had run synchronously inside that call expression itself, before
        // control ever returned to the test method), so SaveAsync and the release genuinely need
        // separate threads to race against each other at all.
        var sut = NewSut();
        await using var lockingConnection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await lockingConnection.OpenAsync();
        var lockingTransaction = await lockingConnection.BeginTransactionAsync();
        await using (var lockCommand = lockingConnection.CreateCommand())
        {
            lockCommand.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)lockingTransaction;
            lockCommand.CommandText = "INSERT INTO FailedMessages (MessageId, Topic, Payload, CreatedAt) VALUES ('lock-holder', 'topic', '{}', '2020-01-01T00:00:00Z');";
            await lockCommand.ExecuteNonQueryAsync();
        }

        var releaseTask = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            await lockingTransaction.CommitAsync();
        });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Task.Run(() => sut.SaveAsync(new FallbackMessage("msg-1", "topic", "{}", DateTime.UtcNow), CancellationToken.None));
        stopwatch.Stop();
        await releaseTask;

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(400), $"Expected SaveAsync to wait for the lock (~500ms), not succeed instantly - took {stopwatch.Elapsed}");
        // 5s matches SqliteMessageFallbackStore's own BusyTimeoutSeconds - well under it confirms
        // SaveAsync succeeded once unblocked, rather than exhausting the whole budget and throwing.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Expected SaveAsync to succeed once the lock was released, not exhaust the full busy timeout - took {stopwatch.Elapsed}");
        var pending = await sut.GetPendingAsync(CancellationToken.None);
        Assert.Contains("msg-1", pending.Select(m => m.MessageId));
    }

    [Fact]
    public async Task GetPendingAsync_MoreThanFiveHundredRows_CapsBatchSize()
    {
        // Regression: during a long broker outage under load, this table can grow into the tens or
        // hundreds of thousands of rows - loading all of them every 30-second retry tick would spike
        // memory and turn one tick into an hours-long serial retry pass. Whatever doesn't fit this
        // batch gets picked up (oldest-first) on a later tick once the backlog shrinks.
        var sut = NewSut();
        var baseTime = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < 505; i++)
        {
            await sut.SaveAsync(new FallbackMessage($"msg-{i:D4}", "topic", "{}", baseTime.AddSeconds(i)), CancellationToken.None);
        }

        var pending = await sut.GetPendingAsync(CancellationToken.None);

        Assert.Equal(500, pending.Count);
        Assert.Equal("msg-0000", pending[0].MessageId);
        Assert.Equal("msg-0499", pending[^1].MessageId);
    }
}
