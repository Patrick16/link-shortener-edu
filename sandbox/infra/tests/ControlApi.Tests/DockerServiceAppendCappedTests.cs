using System.Text;
using ControlApi.Services;

namespace ControlApi.Tests;

public class DockerServiceAppendCappedTests
{
    [Fact]
    public void AppendCapped_UnderLimit_AppendsWholeChunkAndReportsNotTruncated()
    {
        var builder = new StringBuilder();

        var truncated = DockerService.AppendCapped(builder, "hello", maxChars: 100);

        Assert.False(truncated);
        Assert.Equal("hello", builder.ToString());
    }

    [Fact]
    public void AppendCapped_ChunkExceedsLimit_TruncatesAndAppendsMarker()
    {
        var builder = new StringBuilder();

        var truncated = DockerService.AppendCapped(builder, "0123456789", maxChars: 5);

        Assert.True(truncated);
        Assert.StartsWith("01234", builder.ToString());
        Assert.Contains("truncated", builder.ToString());
    }

    [Fact]
    public void AppendCapped_AlreadyAtLimit_ReturnsTruncatedWithoutAppendingAgain()
    {
        var builder = new StringBuilder();
        DockerService.AppendCapped(builder, "0123456789", maxChars: 5);
        var afterFirstTruncation = builder.ToString();

        var truncated = DockerService.AppendCapped(builder, "more text", maxChars: 5);

        Assert.True(truncated);
        Assert.Equal(afterFirstTruncation, builder.ToString());
    }

    [Fact]
    public void AppendCapped_MultipleChunksUnderLimit_AccumulatesAcrossCalls()
    {
        var builder = new StringBuilder();

        DockerService.AppendCapped(builder, "abc", maxChars: 100);
        var truncated = DockerService.AppendCapped(builder, "def", maxChars: 100);

        Assert.False(truncated);
        Assert.Equal("abcdef", builder.ToString());
    }

    [Fact]
    public void AppendCapped_ChunkLandsExactlyOnLimit_TruncatesAndAppendsMarker()
    {
        // Regression: with a plain `>` comparison, a chunk landing exactly on maxChars fell through
        // to the plain-append path (builder ends up exactly at maxChars, reported as "not truncated
        // yet"). The *next* call then hit "already at limit" and returned true immediately, with
        // the truncation marker never appended at all - see the two-call test below for that
        // specific failure mode.
        var builder = new StringBuilder();

        var truncated = DockerService.AppendCapped(builder, "12345", maxChars: 5);

        Assert.True(truncated);
        Assert.StartsWith("12345", builder.ToString());
        Assert.Contains("truncated", builder.ToString());
    }

    [Fact]
    public void AppendCapped_ChunkLandsExactlyOnLimitFollowedByAnotherChunk_MarkerIsNotSilentlyDropped()
    {
        // The actual production shape of the bug: StreamLogsWithProgressAsync only calls
        // AppendCapped again while the previous call reported "not truncated" - a dropped marker on
        // an exact-boundary chunk would otherwise mean every later chunk is silently discarded with
        // no "[output truncated]" marker ever appended, unlike the normal (non-exact-boundary)
        // truncation path.
        var builder = new StringBuilder();

        var firstResult = DockerService.AppendCapped(builder, "12345", maxChars: 5);
        var secondResult = DockerService.AppendCapped(builder, "more text that should never appear", maxChars: 5);

        Assert.True(firstResult);
        Assert.True(secondResult);
        Assert.Contains("truncated", builder.ToString());
        Assert.DoesNotContain("more text", builder.ToString());
    }
}
