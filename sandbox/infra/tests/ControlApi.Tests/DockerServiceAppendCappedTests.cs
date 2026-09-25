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
    public void AppendCapped_ChunkLandsExactlyOnLimit_DoesNotTruncate()
    {
        var builder = new StringBuilder();

        var truncated = DockerService.AppendCapped(builder, "12345", maxChars: 5);

        Assert.False(truncated);
        Assert.Equal("12345", builder.ToString());
    }
}
