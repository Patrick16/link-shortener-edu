using ShortenerService;

namespace ShortenerService.Tests;

public class ClickCountAggregatorTests
{
    [Fact]
    public void DrainPending_NothingAdded_ReturnsEmpty()
    {
        var sut = new ClickCountAggregator();

        var drained = sut.DrainPending();

        Assert.Empty(drained);
    }

    [Fact]
    public void Add_SingleCall_IsReflectedInDrainPending()
    {
        var sut = new ClickCountAggregator();

        sut.Add("abc12345", 1);

        Assert.Equal(1, sut.DrainPending()["abc12345"]);
    }

    [Fact]
    public void Add_MultipleCallsSameHash_Accumulates()
    {
        var sut = new ClickCountAggregator();

        sut.Add("abc12345", 2);
        sut.Add("abc12345", 3);

        Assert.Equal(5, sut.DrainPending()["abc12345"]);
    }

    [Fact]
    public void Add_DifferentHashes_TracksEachIndependently()
    {
        var sut = new ClickCountAggregator();

        sut.Add("hash0001", 2);
        sut.Add("hash0002", 7);

        var drained = sut.DrainPending();
        Assert.Equal(2, drained["hash0001"]);
        Assert.Equal(7, drained["hash0002"]);
    }

    [Fact]
    public void DrainPending_CalledTwice_SecondCallIsEmpty()
    {
        // The whole point of draining (vs. just reading) - a flush tick must never apply the same
        // buffered increment to Postgres twice.
        var sut = new ClickCountAggregator();
        sut.Add("abc12345", 4);

        sut.DrainPending();
        var secondDrain = sut.DrainPending();

        Assert.Empty(secondDrain);
    }

    [Fact]
    public void Add_AfterADrain_StartsAFreshEntryForTheNextDrain()
    {
        var sut = new ClickCountAggregator();
        sut.Add("abc12345", 1);
        sut.DrainPending();

        sut.Add("abc12345", 1);

        Assert.Equal(1, sut.DrainPending()["abc12345"]);
    }

    [Fact]
    public async Task Add_ConcurrentCallsForTheSameHash_LosesNoIncrements()
    {
        // Real concurrency, not a mock - ConcurrentDictionary.AddOrUpdate's own thread-safety is
        // the property actually under test here. 50 tasks x 20 increments each must sum to exactly
        // 1000, with nothing dropped to a race between the "add" and "update" branches.
        var sut = new ClickCountAggregator();
        var tasks = Enumerable.Range(0, 50).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                sut.Add("abc12345", 1);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(1000, sut.DrainPending()["abc12345"]);
    }
}
