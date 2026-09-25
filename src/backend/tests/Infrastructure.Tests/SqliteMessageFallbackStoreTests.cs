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
