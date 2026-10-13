using Infrastructure;

namespace Infrastructure.Tests;

public class TopLinksTests
{
    [Fact]
    public void Window_IsTodayAndTheSixDaysBefore_InDescendingOrder()
    {
        var today = new DateOnly(2026, 10, 13);

        var window = TopLinks.Window(today);

        Assert.Equal(TopLinks.WindowDays, window.Count);
        Assert.Equal(today, window[0]);
        Assert.Equal(new DateOnly(2026, 10, 7), window[^1]);
    }

    [Fact]
    public void Window_CrossesAMonthBoundary()
    {
        var window = TopLinks.Window(new DateOnly(2026, 11, 2));

        Assert.Contains(new DateOnly(2026, 10, 27), window);
    }

    [Fact]
    public void DayKey_IsScopedToUserAndDay()
    {
        var userId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var key = TopLinks.DayKey(userId, new DateOnly(2026, 10, 13));

        Assert.Equal("clicks:user:11111111222233334444555555555555:day:20261013", key);
    }

    [Fact]
    public void DayKey_TtlOutlivesTheWindow()
    {
        // A read that lands just after midnight must still find all seven days, not lose the oldest
        // one to expiry - so the TTL has to be longer than the window it serves.
        Assert.True(TopLinks.DayKeyTtlDays > TopLinks.WindowDays);
    }
}
