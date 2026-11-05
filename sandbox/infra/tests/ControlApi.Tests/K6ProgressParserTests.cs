using ControlApi.Services.K6;

namespace ControlApi.Tests;

// The whole point of extracting this out of K6ContainerRunner.StreamLogsWithProgressAsync - these
// cases used to only be reachable through the full Docker-dependent RunTrafficAsync path.
public class K6ProgressParserTests
{
    [Fact]
    public void TryParse_PlainSecondsFormat_ParsesElapsedVusAndIterations()
    {
        var result = K6ProgressParser.TryParse("running (12.3s), 5/10 VUs, 42 complete");

        Assert.NotNull(result);
        Assert.Equal(12.3, result!.Value.ElapsedSeconds, precision: 3);
        Assert.Equal(5, result.Value.ActiveVus);
        Assert.Equal(42, result.Value.Iterations);
    }

    [Fact]
    public void TryParse_MinutesAndSecondsFormat_ElapsedIncludesTheMinutes()
    {
        var result = K6ProgressParser.TryParse("running (1m05.0s), 8/10 VUs, 100 complete");

        Assert.NotNull(result);
        Assert.Equal(65.0, result!.Value.ElapsedSeconds, precision: 3);
        Assert.Equal(8, result.Value.ActiveVus);
        Assert.Equal(100, result.Value.Iterations);
    }

    [Fact]
    public void TryParse_HoursMinutesAndSecondsFormat_ElapsedIncludesBoth()
    {
        var result = K6ProgressParser.TryParse("running (1h02m03.0s), 10/10 VUs, 500 complete");

        Assert.NotNull(result);
        Assert.Equal(3723.0, result!.Value.ElapsedSeconds, precision: 3);
        Assert.Equal(10, result.Value.ActiveVus);
        Assert.Equal(500, result.Value.Iterations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("some unrelated k6 log line")]
    [InlineData("WARN[0012] request failed")]
    public void TryParse_NotAProgressLine_ReturnsNull(string line)
    {
        Assert.Null(K6ProgressParser.TryParse(line));
    }
}
