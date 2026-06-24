using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Blazor;
using Xunit;

namespace HomeBlaze.History.Blazor.Tests;

public class PropertyHistoryChartModelTests
{
    [Theory]
    [InlineData(1, 0, 0, 30)]    // 1h range / 200 = 18s -> 30s
    [InlineData(168, 1, 0, 0)]   // 7d range / 200 = ~50m -> 1h
    [InlineData(720, 6, 0, 0)]   // 30d range / 200 = ~3.6h -> 6h
    public void WhenAutoBucket_ThenRoundsRangeOver200ToNiceInterval(int rangeHours, int h, int m, int s)
    {
        // Act
        var bucket = PropertyHistoryChartModel.AutoBucket(TimeSpan.FromHours(rangeHours));

        // Assert
        Assert.Equal(new TimeSpan(h, m, s), bucket);
    }

    [Fact]
    public void WhenNumericNonCumulative_ThenAllAggregationsOfferedWithTwaFirst()
    {
        // Arrange
        var union = new HashSet<string>(HistoryEligibilityUnionAll(), StringComparer.Ordinal);

        // Act
        var result = PropertyHistoryChartModel.GateAggregations(ValueColumn.Double, isCumulative: false, union);

        // Assert
        Assert.Equal(HistoryAggregations.TimeWeightedAverage, result[0]);
        Assert.Contains(HistoryAggregations.StandardDeviation, result);
        Assert.Contains(HistoryAggregations.Sum, result);
    }

    [Fact]
    public void WhenCumulative_ThenExcludesAverageAndSum()
    {
        // Act
        var result = PropertyHistoryChartModel.GateAggregations(
            ValueColumn.Long, isCumulative: true, HistoryEligibilityUnionAll());

        // Assert
        Assert.DoesNotContain(HistoryAggregations.TimeWeightedAverage, result);
        Assert.DoesNotContain(HistoryAggregations.Sum, result);
        Assert.DoesNotContain(HistoryAggregations.SampleAverage, result);
        Assert.Contains(HistoryAggregations.Minimum, result);
        Assert.Contains(HistoryAggregations.Count, result);
    }

    [Fact]
    public void WhenJsonColumn_ThenOnlyLastFirstCount()
    {
        // Act
        var result = PropertyHistoryChartModel.GateAggregations(
            ValueColumn.Json, isCumulative: false, HistoryEligibilityUnionAll());

        // Assert
        Assert.Equal(
            new[] { HistoryAggregations.Last, HistoryAggregations.First, HistoryAggregations.Count }.OrderBy(x => x),
            result.OrderBy(x => x));
    }

    [Fact]
    public void WhenStoreUnionMissingAnAggregation_ThenItIsFilteredOutUnlessAlwaysAvailable()
    {
        // Arrange - a store union that supports only Last and Minimum; Sum should drop, Last/Count always stay.
        var union = new HashSet<string>(StringComparer.Ordinal)
        {
            HistoryAggregations.Last, HistoryAggregations.Minimum
        };

        // Act
        var result = PropertyHistoryChartModel.GateAggregations(ValueColumn.Double, isCumulative: false, union);

        // Assert
        Assert.Contains(HistoryAggregations.Minimum, result);
        Assert.Contains(HistoryAggregations.Last, result);
        Assert.Contains(HistoryAggregations.Count, result);      // AlwaysAvailable, kept even though not in union
        Assert.DoesNotContain(HistoryAggregations.Sum, result);  // not in union, not AlwaysAvailable
    }

    [Fact]
    public void WhenPointsContainNulls_ThenSplitsIntoContiguousRuns()
    {
        // Arrange - [v, null, v, v] -> runs of sizes [1, 2]
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var points = new[]
        {
            new HistoryPoint(t, 1d, null),
            new HistoryPoint(t.AddSeconds(10), null, null),
            new HistoryPoint(t.AddSeconds(20), 3d, null),
            new HistoryPoint(t.AddSeconds(30), 4d, null),
        };

        // Act
        var runs = PropertyHistoryChartModel.SplitIntoGapRuns(points);

        // Assert
        Assert.Equal(2, runs.Count);
        Assert.Single(runs[0]);
        Assert.Equal(2, runs[1].Count);
        Assert.Equal(3d, runs[1][0].Number);
    }

    // Every numeric store in v1 supports every aggregation; this models that union for the gating tests.
    private static HashSet<string> HistoryEligibilityUnionAll() => new(StringComparer.Ordinal)
    {
        HistoryAggregations.Last, HistoryAggregations.First, HistoryAggregations.SampleAverage,
        HistoryAggregations.TimeWeightedAverage, HistoryAggregations.Minimum, HistoryAggregations.Maximum,
        HistoryAggregations.Sum, HistoryAggregations.Count, HistoryAggregations.StandardDeviation
    };
}
