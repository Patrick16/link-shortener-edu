using TrafficService;

namespace TrafficService.Tests;

public class PartitionPlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GetPartitionsToEnsure_ReturnsCurrentMonthThroughLookAhead_Inclusive()
    {
        var result = PartitionPlanner.GetPartitionsToEnsure(Now, lookAheadMonths: 3);

        Assert.Equal(4, result.Count);
        Assert.Equal(
            ["clicks_y2026m10", "clicks_y2026m11", "clicks_y2026m12", "clicks_y2027m01"],
            result.Select(p => p.Name));
    }

    [Fact]
    public void GetPartitionsToEnsure_EachRangeCoversExactlyOneCalendarMonth()
    {
        var result = PartitionPlanner.GetPartitionsToEnsure(Now, lookAheadMonths: 1);

        var october = result.Single(p => p.Name == "clicks_y2026m10");
        Assert.Equal(new DateOnly(2026, 10, 1), october.Start);
        Assert.Equal(new DateOnly(2026, 11, 1), october.End);
    }

    [Fact]
    public void GetPartitionsToEnsure_ZeroLookAhead_ReturnsOnlyCurrentMonth()
    {
        var result = PartitionPlanner.GetPartitionsToEnsure(Now, lookAheadMonths: 0);

        Assert.Single(result);
        Assert.Equal("clicks_y2026m10", result[0].Name);
    }

    [Fact]
    public void GetPartitionsToDrop_KeepsRetentionMonths_DropsOlder()
    {
        // retentionMonths=3 on 2026-10-09 should keep Aug/Sep/Oct and drop everything before Aug.
        var existing = new[]
        {
            "clicks_y2026m06", // ended 2026-07-01 -> drop
            "clicks_y2026m07", // ended 2026-08-01 -> drop (cutoff is inclusive)
            "clicks_y2026m08", // ended 2026-09-01 -> keep
            "clicks_y2026m09", // keep
            "clicks_y2026m10", // keep (current month)
            "clicks_y2026m11", // future -> keep
        };

        var toDrop = PartitionPlanner.GetPartitionsToDrop(Now, retentionMonths: 3, existing);

        Assert.Equal(["clicks_y2026m06", "clicks_y2026m07"], toDrop);
    }

    [Fact]
    public void GetPartitionsToDrop_NeverDropsDefaultPartition()
    {
        var toDrop = PartitionPlanner.GetPartitionsToDrop(Now, retentionMonths: 1, ["clicks_default"]);

        Assert.Empty(toDrop);
    }

    [Theory]
    [InlineData("clicks_default")]
    [InlineData("clicks_y2026")]
    [InlineData("clicks_y2026m1")]
    [InlineData("clicks_yYYYYmMM")]
    [InlineData("some_other_table")]
    [InlineData("clicks_y2026m13")]
    public void GetPartitionsToDrop_IgnoresNamesThatDoNotMatchTheExpectedShape(string name)
    {
        // Far-future cutoff so "would be dropped if parsed" is guaranteed - isolates the
        // name-validation branch from the date-cutoff branch.
        var toDrop = PartitionPlanner.GetPartitionsToDrop(Now, retentionMonths: 1000, [name]);

        Assert.Empty(toDrop);
    }

    [Fact]
    public void GetPartitionsToDrop_BoundaryMonth_IsKeptNotDropped()
    {
        // The oldest month that should still be retained (exactly retentionMonths back,
        // inclusive) must never show up in the drop list - off-by-one here would quietly eat an
        // extra month of real data every tick.
        var toDrop = PartitionPlanner.GetPartitionsToDrop(Now, retentionMonths: 3, ["clicks_y2026m08"]);

        Assert.Empty(toDrop);
    }
}
