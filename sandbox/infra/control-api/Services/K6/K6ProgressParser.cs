using System.Globalization;
using System.Text.RegularExpressions;

namespace ControlApi.Services.K6;

// Extracted out of K6ContainerRunner.StreamLogsWithProgressAsync as a pure, static, easily
// unit-tested piece of an otherwise heavily Docker-API-coupled method (same reasoning as
// AppendCapped getting the same treatment) - see .notes/PLAN.md's Simplification Backlog item 3.
public static class K6ProgressParser
{
    // k6 picks the elapsed-time format by the *total configured test duration*, not the current
    // elapsed value: under a minute it's plain "12.3s" for the whole run, but a run configured for
    // 60s or more prints "0m01.0s", "1m05.0s", etc. from the very first line onward - confirmed
    // against real runs at 10s vs 65s (identical scripts, only --duration differed). Groups:
    // 1=hours (optional), 2=minutes (optional), 3=seconds, 4=active VUs, 5=max VUs, 6=iterations.
    private static readonly Regex K6ProgressLine =
        new(@"running \((?:(\d+)h)?(?:(\d+)m)?(\d+(?:\.\d+)?)s\), (\d+)/(\d+) VUs, (\d+) complete", RegexOptions.Compiled);

    public readonly record struct Match(double ElapsedSeconds, int ActiveVus, long Iterations);

    // Null means the line isn't one of k6's periodic status lines (every other line k6 prints -
    // request logs, summary output, etc.) - the caller's loop just skips it, same as before this
    // was extracted.
    public static Match? TryParse(string line)
    {
        var match = K6ProgressLine.Match(line);
        if (!match.Success)
        {
            return null;
        }

        var hours = match.Groups[1].Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = match.Groups[2].Success ? double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var elapsed = hours * 3600 + minutes * 60 + seconds;
        var activeVus = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
        var iterations = long.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture);

        return new Match(elapsed, activeVus, iterations);
    }
}
