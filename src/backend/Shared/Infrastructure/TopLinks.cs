namespace Infrastructure;

// Shared between RedirectApi (writes one click at a time) and ReportingApi (reads the rolling top
// list and pushes it over SignalR) - the key format and the window definition have to agree on both
// sides or the two services silently disagree about which day a click belongs to.
public static class TopLinks
{
    public const int WindowDays = 7;
    public const int TopCount = 10;

    // Keeps one more day than the window, so a read that lands just after midnight UTC still finds
    // the full 7-day span instead of losing its oldest day to expiry.
    public const int DayKeyTtlDays = WindowDays + 1;

    // Published on every click that has an owner, carrying the owner's id. ReportingApi subscribes
    // and decides whether that user's connected dashboard needs a fresh push.
    public const string ChangedChannel = "clicks:user:changed";

    public static string DayKey(Guid userId, DateOnly day) =>
        $"clicks:user:{userId:N}:day:{day:yyyyMMdd}";

    // Today plus the six days before it, in UTC - a sliding window, because it is recomputed from
    // the day buckets on every read rather than stored as one fixed snapshot.
    public static IReadOnlyList<DateOnly> Window(DateOnly today) =>
        Enumerable.Range(0, WindowDays).Select(offset => today.AddDays(-offset)).ToList();
}

public sealed record TopLinkEntry(string Hash, long Clicks);
