using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Blazor;
using Xunit;

namespace HomeBlaze.History.Blazor.Tests;

public class PropertyHistoryChartModelTests
{
    private static readonly IReadOnlyList<string> NumberAggregations = HistoryAggregations.All;

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
    public void WhenAutoBucketCoverageNarrowerThanRange_ThenClampsBucketTowardsCoverage()
    {
        // Arrange - 24h range but only 5 minutes of recorded data.
        var range = TimeSpan.FromHours(24);
        var coverage = TimeSpan.FromMinutes(5);

        // Act
        var bucket = PropertyHistoryChartModel.AutoBucket(range, coverage);

        // Assert - finer than the 24h-only choice so the recorded data still fills buckets, but not so
        // fine that the range needs more intervals than the query returns. 24h / 999 = ~86s, which rounds
        // up to the 2m rung; the coverage's own 5m / 200 = 1.5s is the floor this deliberately overrides.
        Assert.Equal(TimeSpan.FromMinutes(2), bucket);
        Assert.True(bucket < PropertyHistoryChartModel.AutoBucket(range));
    }

    [Fact]
    public void WhenAutoBucketCoverageWiderThanRange_ThenNoClamp()
    {
        // Arrange - coverage wider than the selected range, so the range drives the bucket.
        var range = TimeSpan.FromHours(1);

        // Act
        var bucket = PropertyHistoryChartModel.AutoBucket(range, availableCoverage: TimeSpan.FromHours(6));

        // Assert: 1h / 200 = 18s, which rounds up to the 30s rung. A golden value, because
        // comparing against AutoBucket would move with the code and pass even if the clamp were gone.
        Assert.Equal(TimeSpan.FromSeconds(30), bucket);
    }

    [Theory]
    [InlineData(1, 2)]        // a store recording for two minutes, asked for an hour
    [InlineData(1, 30)]
    [InlineData(24, 5)]       // the case that shipped a "too much history" notice over an empty chart
    [InlineData(24, 60)]
    [InlineData(168, 5)]
    [InlineData(720, 1)]      // 30 days requested after one minute of recording
    public void WhenCoverageIsFarNarrowerThanTheRange_ThenAutoStillFitsThePointCap(
        int rangeHours, int coverageMinutes)
    {
        // Arrange - the clamp to coverage exists so a fresh system renders at all, but the query still
        // spans the whole range, so a bucket sized from coverage alone divided that range into far more
        // intervals than the cap. Every such chart then reported truncated history while holding almost none.
        var range = TimeSpan.FromHours(rangeHours);
        var coverage = TimeSpan.FromMinutes(coverageMinutes);

        // Act
        var bucket = PropertyHistoryChartModel.AutoBucket(range, coverage);

        // Assert - mirrors BucketAlignment.FirstBucketStart: the first bucket starts at or before the
        // query's start, so the range can span one interval more than the division alone suggests.
        var bucketCount = 1 + (range.Ticks - 1) / bucket.Ticks + 1;
        Assert.True(
            bucketCount <= PropertyHistoryChartModel.MaxPoints,
            $"{rangeHours}h range over {coverageMinutes}m coverage chose a {bucket} bucket, " +
            $"which needs {bucketCount} intervals for a cap of {PropertyHistoryChartModel.MaxPoints}.");
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(2400)]   // a custom range of 2020-01-01 to today
    [InlineData(4000)]
    public void WhenTheRangeSpansYears_ThenAutoStillFitsThePointCap(int days)
    {
        // Arrange - the cap floor needs a rung at least range/999, so a ladder topping out at one day
        // could not satisfy it beyond 999 days. Past that, Auto picked the coarsest bucket in existence
        // and the chart still reported truncation, with no coarser period available to escape to. The
        // custom range picker reaches this easily.
        var range = TimeSpan.FromDays(days);

        // Act
        var bucket = PropertyHistoryChartModel.AutoBucket(range, TimeSpan.FromMinutes(5));

        // Assert - mirrors BucketAlignment.FirstBucketStart, which aligns the first bucket at or before
        // the query start, so the range can span one interval more than the division suggests.
        var bucketCount = 1 + (range.Ticks - 1) / bucket.Ticks + 1;
        Assert.True(
            bucketCount <= PropertyHistoryChartModel.MaxPoints,
            $"{days}d chose a {bucket} bucket, needing {bucketCount} intervals.");
    }

    [Fact]
    public void WhenTheRangeIsAbsurd_ThenAutoDoesNotOverflowIntoNoCapAtAll()
    {
        // Arrange & Act - the ceiling division overflows above long.MaxValue - MaxPoints, and a negative
        // floor is silently discarded by Math.Max, which restored exactly the uncapped behaviour.
        var bucket = PropertyHistoryChartModel.AutoBucket(TimeSpan.MaxValue, TimeSpan.FromMinutes(5));

        // Assert - the coarsest rung, not the two seconds the coverage alone would have asked for.
        Assert.Equal(TimeSpan.FromDays(30), bucket);
    }

    [Fact]
    public void WhenTheRangeIsFullyCovered_ThenThePointCapDoesNotChangeTheBucket()
    {
        // Arrange - the cap floor is range / 999 and the resolution target is range / 200, so the target
        // always wins when coverage is not clamping. The fix must not coarsen the ordinary case.
        foreach (var hours in new[] { 1, 4, 24, 168, 720 })
        {
            var range = TimeSpan.FromHours(hours);

            // Act
            var covered = PropertyHistoryChartModel.AutoBucket(range, range);
            var rangeOnly = PropertyHistoryChartModel.AutoBucket(range);

            // Assert
            Assert.Equal(rangeOnly, covered);
        }
    }

    [Fact]
    public void WhenAutoBucketCoverageNull_ThenMatchesRangeOnlyBehavior()
    {
        // Act
        var bucket = PropertyHistoryChartModel.AutoBucket(TimeSpan.FromHours(24), availableCoverage: null);

        // Assert - unchanged from the 24h range-only expectation (24h / 200 = 7.2m -> 10m).
        Assert.Equal(TimeSpan.FromMinutes(10), bucket);
    }

    [Fact]
    public void WhenResolveBucketAutoWithNarrowCoverage_ThenReturnsClampedBucket()
    {
        // Arrange
        var auto = PropertyHistoryChartModel.Periods[0];
        var range = TimeSpan.FromHours(24);
        var coverage = TimeSpan.FromMinutes(5);

        // Act
        var bucket = PropertyHistoryChartModel.ResolveBucket(auto, range, coverage);

        // Assert: pulled down by the 5 minute coverage, but held at the point cap's floor of 24h / 999,
        // which rounds up to the 2m rung. A golden value for the same reason as above.
        Assert.Equal(TimeSpan.FromMinutes(2), bucket);
    }

    [Theory]
    [InlineData(10, 10d, "s")]
    [InlineData(30, 30d, "s")]
    [InlineData(120, 2d, "m")]
    [InlineData(5 * 60, 5d, "m")]
    [InlineData(60 * 60, 1d, "h")]
    [InlineData(90 * 60, 1.5, "h")]
    [InlineData(24 * 60 * 60, 1d, "d")]
    [InlineData(36 * 60 * 60, 1.5, "d")]
    [InlineData(7 * 24 * 60 * 60, 7d, "d")]
    public void WhenADurationIsFormatted_ThenItIsInItsLargestUnitUpToDays(int totalSeconds, double number, string unit)
    {
        // Act
        var label = PropertyHistoryChartModel.FormatDuration(TimeSpan.FromSeconds(totalSeconds));

        // Assert
        Assert.Equal(number.ToString("0.#") + unit, label);
    }

    [Theory]
    [InlineData(HistoryAggregations.TimeWeightedAverage, "time-weighted average")]
    [InlineData(HistoryAggregations.SampleAverage, "count-weighted mean")]
    [InlineData(HistoryAggregations.Count, "sample count")]
    [InlineData(HistoryAggregations.StandardDeviation, "sample std. deviation")]
    public void WhenDescribeAggregation_ThenReturnsShortDescription(string aggregation, string expected)
    {
        // Act
        var description = PropertyHistoryChartModel.DescribeAggregation(aggregation);

        // Assert
        Assert.Equal(expected, description);
    }

    [Fact]
    public void WhenDescribePeriodAutoWithResolvedBucket_ThenReturnsAboutBucket()
    {
        // Arrange
        var auto = PropertyHistoryChartModel.Periods[0];

        // Act
        var description = PropertyHistoryChartModel.DescribePeriod(auto, TimeSpan.FromSeconds(15));

        // Assert
        Assert.Equal("about 15s", description);
    }

    [Fact]
    public void WhenDescribePeriodRaw_ThenReturnsRawSamples()
    {
        // Arrange
        var none = PropertyHistoryChartModel.Periods.Single(period => period.Label == "None (raw samples)");

        // Act
        var description = PropertyHistoryChartModel.DescribePeriod(none, resolvedBucket: null);

        // Assert
        Assert.Equal("raw samples", description);
    }

    [Fact]
    public void WhenDescribePeriodExplicit_ThenReturnsBucketLabelWithBuckets()
    {
        // Arrange
        var tenSeconds = PropertyHistoryChartModel.Periods.Single(period => period.Label == "10s");

        // Act
        var description = PropertyHistoryChartModel.DescribePeriod(tenSeconds, tenSeconds.Bucket);

        // Assert
        Assert.Equal("10s buckets", description);
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
        var runs = PropertyHistoryChartModel.SplitIntoGapRuns(points, singlePointWidth: null, points[^1].Timestamp);

        // Assert
        Assert.Equal(2, runs.Count);
        Assert.Equal(new[] { t, t.AddSeconds(10) }, runs[0].Select(point => point.Timestamp));
        Assert.All(runs[0], point => Assert.Equal(1d, point.Number));
        Assert.Equal(2, runs[1].Count);
        Assert.Equal(3d, runs[1][0].Number);
    }

    [Fact]
    public void WhenARawSinglePointIsLastInTheSeries_ThenItHoldsToTheWindowEnd()
    {
        // Arrange
        var start = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        HistoryPoint[] points = [new HistoryPoint(start, 4d, null)];

        // Act
        var runs = PropertyHistoryChartModel.SplitIntoGapRuns(points, singlePointWidth: null, start.AddHours(1));

        // Assert
        var heldRun = Assert.Single(runs);
        Assert.Equal(new[] { start, start.AddHours(1) }, heldRun.Select(point => point.Timestamp));
        Assert.All(heldRun, point => Assert.Equal(4d, point.Number));
    }

    [Fact]
    public void WhenARunHasASinglePoint_ThenItIsWidenedIntoAFlatSegment()
    {
        // Arrange
        var start = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        HistoryPoint[] points = [new HistoryPoint(start, 4d, null)];

        // Act
        var runs = PropertyHistoryChartModel.SplitIntoGapRuns(points, TimeSpan.FromMinutes(10), start.AddHours(1));

        // Assert
        var widenedRun = Assert.Single(runs);
        Assert.Equal(new[] { start, start.AddMinutes(10) }, widenedRun.Select(point => point.Timestamp));
        Assert.All(widenedRun, point => Assert.Equal(4d, point.Number));
    }

    [Fact]
    public void WhenASinglePointIsNearTheWindowEnd_ThenTheSegmentStopsAtTheEnd()
    {
        // Arrange
        var start = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        HistoryPoint[] points = [new HistoryPoint(start, 4d, null)];

        // Act
        var runs = PropertyHistoryChartModel.SplitIntoGapRuns(points, TimeSpan.FromMinutes(10), start.AddMinutes(3));

        // Assert
        Assert.Equal(start.AddMinutes(3), Assert.Single(runs)[^1].Timestamp);
    }

    [Fact]
    public void WhenASinglePointIsFollowedByANullPointWithinTheWidth_ThenTheSegmentStopsAtTheNullPoint()
    {
        // Arrange
        var start = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        HistoryPoint[] points =
        [
            new HistoryPoint(start, 4d, null),
            new HistoryPoint(start.AddMinutes(2), null, null),
            new HistoryPoint(start.AddMinutes(20), 5d, null),
            new HistoryPoint(start.AddMinutes(21), 6d, null)
        ];

        // Act
        var runs = PropertyHistoryChartModel.SplitIntoGapRuns(points, TimeSpan.FromMinutes(10), start.AddHours(1));

        // Assert
        Assert.Equal(2, runs.Count);
        Assert.Equal(new[] { start, start.AddMinutes(2) }, runs[0].Select(point => point.Timestamp));
        Assert.All(runs[0], point => Assert.Equal(4d, point.Number));
    }

    [Fact]
    public void WhenARunHasSeveralPoints_ThenItIsNotWidened()
    {
        // Arrange
        var start = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        HistoryPoint[] points = [new HistoryPoint(start, 1d, null), new HistoryPoint(start.AddMinutes(1), 2d, null)];

        // Act
        var runs = PropertyHistoryChartModel.SplitIntoGapRuns(points, TimeSpan.FromMinutes(10), start.AddHours(1));

        // Assert
        Assert.Equal(points, Assert.Single(runs));
    }

    [Fact]
    public void WhenPeriods_ThenContainsExpectedLabelsInOrder()
    {
        // Act
        var labels = PropertyHistoryChartModel.Periods.Select(period => period.Label).ToArray();

        // Assert
        Assert.Equal(
            new[] { "Auto", "None (raw samples)", "1s", "10s", "60s", "5m", "10m", "15m", "1h", "4h", "6h", "12h", "24h" },
            labels);
    }

    [Fact]
    public void WhenPeriods_ThenFirstIsAutoAndIsTheOnlyAutoEntry()
    {
        // Act
        var auto = PropertyHistoryChartModel.Periods[0];

        // Assert
        Assert.Equal("Auto", auto.Label);
        Assert.True(auto.IsAuto);
        Assert.Null(auto.Bucket);
        Assert.Single(PropertyHistoryChartModel.Periods, period => period.IsAuto);
    }

    [Fact]
    public void WhenPeriods_ThenNoneHasNullBucketAndIsNotAuto()
    {
        // Act
        var none = PropertyHistoryChartModel.Periods.Single(period => period.Label == "None (raw samples)");

        // Assert
        Assert.Null(none.Bucket);
        Assert.False(none.IsAuto);
    }

    [Fact]
    public void WhenPeriodsExplicit_ThenFiveMinutesHasMatchingBucket()
    {
        // Act
        var fiveMinutes = PropertyHistoryChartModel.Periods.Single(period => period.Label == "5m");

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(5), fiveMinutes.Bucket);
        Assert.False(fiveMinutes.IsAuto);
    }

    [Fact]
    public void WhenResolveBucketAuto_ThenReturnsAutoBucketForRange()
    {
        // Arrange
        var range = TimeSpan.FromHours(1);
        var auto = PropertyHistoryChartModel.Periods[0];

        // Act
        var bucket = PropertyHistoryChartModel.ResolveBucket(auto, range);

        // Assert
        Assert.Equal(PropertyHistoryChartModel.AutoBucket(range), bucket);
    }

    [Fact]
    public void WhenResolveBucketNone_ThenReturnsNull()
    {
        // Arrange
        var none = PropertyHistoryChartModel.Periods.Single(period => period.Label == "None (raw samples)");

        // Act
        var bucket = PropertyHistoryChartModel.ResolveBucket(none, TimeSpan.FromHours(1));

        // Assert
        Assert.Null(bucket);
    }

    [Fact]
    public void WhenResolveBucketExplicit_ThenReturnsThatBucket()
    {
        // Arrange
        var fiveMinutes = PropertyHistoryChartModel.Periods.Single(period => period.Label == "5m");

        // Act
        var bucket = PropertyHistoryChartModel.ResolveBucket(fiveMinutes, TimeSpan.FromHours(24));

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(5), bucket);
    }

    // Treats a wall-clock pick as already being in UTC, isolating the day-inclusion logic from time-zone conversion.
    private static DateTimeOffset AsUtc(DateTime wallClock) => new(wallClock, TimeSpan.Zero);

    [Fact]
    public void WhenCustomRangeSingleDay_ThenIncludesWholeSelectedDay()
    {
        // Arrange - same From and To day; To is the end of that day so the window spans the full day, not zero.
        var day = new DateTime(2026, 6, 27);

        // Act
        var (from, to) = PropertyHistoryChartModel.ResolveCustomRange(day, day, DateTimeOffset.UtcNow, AsUtc);

        // Assert - a single-day pick is a full day, and the dialog's "to must be after from" guard accepts it.
        Assert.True(to > from);
        Assert.Equal(TimeSpan.FromDays(1), to - from);
    }

    [Fact]
    public void WhenCustomRangeToAfterFrom_ThenToIsStartOfDayAfterSelectedTo()
    {
        // Arrange
        var from = new DateTime(2026, 6, 25);
        var to = new DateTime(2026, 6, 27);

        // Act
        var (resolvedFrom, resolvedTo) = PropertyHistoryChartModel.ResolveCustomRange(
            from, to, DateTimeOffset.UtcNow, AsUtc);

        // Assert - From is start-of-day; the selected To day (27th) is fully included, so the window ends on the 28th.
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 0, 0, 0, TimeSpan.Zero), resolvedFrom);
        Assert.Equal(new DateTimeOffset(2026, 6, 28, 0, 0, 0, TimeSpan.Zero), resolvedTo);
    }

    [Fact]
    public void WhenCustomRangeInverted_ThenToIsNotAfterFromSoGuardRejects()
    {
        // Arrange - From day later than To day.
        var from = new DateTime(2026, 6, 27);
        var to = new DateTime(2026, 6, 25);

        // Act
        var (resolvedFrom, resolvedTo) = PropertyHistoryChartModel.ResolveCustomRange(
            from, to, DateTimeOffset.UtcNow, AsUtc);

        // Assert - end-of-day shift on To (26th) is still before From (27th), so the to <= from guard rejects it.
        Assert.True(resolvedTo <= resolvedFrom);
    }

    [Fact]
    public void WhenCustomRangeUnset_ThenFallsBackToLastHourEndingAtNow()
    {
        // Arrange - neither picker has a value yet (just switched to Custom).
        var now = new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

        // Act
        var (from, to) = PropertyHistoryChartModel.ResolveCustomRange(null, null, now, AsUtc);

        // Assert
        Assert.Equal(now, to);
        Assert.Equal(now.AddHours(-1), from);
    }

    // Every numeric store in v1 supports every aggregation; this models that union for the gating tests.
    private static HashSet<string> HistoryEligibilityUnionAll() => new(StringComparer.Ordinal)
    {
        HistoryAggregations.Last, HistoryAggregations.First, HistoryAggregations.SampleAverage,
        HistoryAggregations.TimeWeightedAverage, HistoryAggregations.Minimum, HistoryAggregations.Maximum,
        HistoryAggregations.Sum, HistoryAggregations.Count, HistoryAggregations.StandardDeviation
    };

    [Fact]
    public void WhenRangePresetsAreListed_ThenTheyGoFromTheShortestToTheLongest()
    {
        // Act
        var shortNames = PropertyHistoryChartModel.RangePresets.Select(preset => preset.ShortName).ToArray();

        // Assert
        Assert.Equal(["1h", "6h", "24h", "7d", "30d"], shortNames);
        Assert.Equal("Last hour", PropertyHistoryChartModel.RangePresets[0].Label);
    }

    [Theory]
    [InlineData("1h", 1)]
    [InlineData("6h", 6)]
    [InlineData("24h", 24)]
    [InlineData("7d", 168)]
    [InlineData("30d", 720)]
    [InlineData("24H", 24)]
    public void WhenRangeShortNameIsKnown_ThenTryGetRangeReturnsItsSpan(string shortName, int expectedHours)
    {
        // Act
        var found = PropertyHistoryChartModel.TryGetRange(shortName, out var range);

        // Assert
        Assert.True(found);
        Assert.Equal(TimeSpan.FromHours(expectedHours), range);
    }

    [Theory]
    [InlineData("2h")]
    [InlineData("Custom")]
    [InlineData("")]
    [InlineData(null)]
    public void WhenRangeShortNameIsUnknown_ThenTryGetRangeFails(string? shortName)
    {
        // Act
        var found = PropertyHistoryChartModel.TryGetRange(shortName, out _);

        // Assert
        Assert.False(found);
    }

    [Theory]
    [InlineData("Auto", true, false, null)]
    [InlineData("Raw", false, true, null)]
    [InlineData("raw", false, true, null)]
    [InlineData("1s", false, false, 1)]
    [InlineData("60s", false, false, 60)]
    [InlineData("5m", false, false, 300)]
    [InlineData("24h", false, false, 86400)]
    public void WhenPeriodShortNameIsKnown_ThenTryGetPeriodReturnsThePeriod(
        string shortName, bool expectedIsAuto, bool expectedIsRaw, int? expectedBucketSeconds)
    {
        // Arrange
        TimeSpan? expectedBucket = expectedBucketSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

        // Act
        var found = PropertyHistoryChartModel.TryGetPeriod(shortName, out var period);

        // Assert
        Assert.True(found);
        Assert.Equal(expectedIsAuto, period.IsAuto);
        Assert.Equal(expectedIsRaw, period.IsRaw);
        Assert.Equal(expectedBucket, period.Bucket);
    }

    [Theory]
    [InlineData("2m")]
    [InlineData("None")]
    [InlineData("")]
    [InlineData(null)]
    public void WhenPeriodShortNameIsUnknown_ThenTryGetPeriodFails(string? shortName)
    {
        // Act
        var found = PropertyHistoryChartModel.TryGetPeriod(shortName, out _);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public void WhenEveryPeriodIsLookedUpByItsShortName_ThenItIsFoundAndTheNamesAreDistinct()
    {
        // Act & Assert
        Assert.All(PropertyHistoryChartModel.Periods, period =>
        {
            Assert.True(PropertyHistoryChartModel.TryGetPeriod(period.ShortName, out var match));
            Assert.Equal(period, match);
        });
        Assert.Equal(
            PropertyHistoryChartModel.Periods.Count,
            PropertyHistoryChartModel.Periods.Select(period => period.ShortName.ToUpperInvariant()).Distinct().Count());
    }

    [Theory]
    [InlineData("Maximum", HistoryAggregations.Maximum)]
    [InlineData("timeweightedaverage", HistoryAggregations.TimeWeightedAverage)]
    [InlineData("STANDARDDEVIATION", HistoryAggregations.StandardDeviation)]
    [InlineData("average", HistoryAggregations.TimeWeightedAverage)]
    public void WhenAnAggregationNameIsKnownInAnyCase_ThenTryGetAggregationReturnsItsIdentifier(string name, string expected)
    {
        // Act
        var found = PropertyHistoryChartModel.TryGetAggregation(name, out var aggregation);

        // Assert
        Assert.True(found);
        Assert.Equal(expected, aggregation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Median")]
    public void WhenAnAggregationNameIsUnknown_ThenTryGetAggregationFails(string? name)
    {
        // Act
        var found = PropertyHistoryChartModel.TryGetAggregation(name, out _);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public void WhenTheDialogOpensWithoutSettings_ThenItShowsTheLastHourAutoAndTheFirstOfferedAggregation()
    {
        // Act
        var selection = PropertyHistoryChartModel.GetInitialSelection(null, NumberAggregations, isDiscrete: false);

        // Assert
        Assert.Equal(new PropertyHistoryChartModel.DialogSelection(0, 0, HistoryAggregations.TimeWeightedAverage), selection);
    }

    [Fact]
    public void WhenTheDialogOfADiscretePropertyOpensWithoutSettings_ThenItShowsRawSamplesAndTheLastValue()
    {
        // Arrange
        string[] offered = [HistoryAggregations.Last, HistoryAggregations.First, HistoryAggregations.Count];

        // Act
        var selection = PropertyHistoryChartModel.GetInitialSelection(null, offered, isDiscrete: true);

        // Assert
        Assert.True(PropertyHistoryChartModel.Periods[selection.PeriodIndex].IsRaw);
        Assert.Equal(HistoryAggregations.Last, selection.Aggregation);
    }

    [Fact]
    public void WhenTheDialogOpensWithTheSettingsOfAChart_ThenItSelectsThePresetThePeriodAndTheAggregation()
    {
        // Arrange
        PropertyHistoryChartModel.TryGetPeriod("10m", out var period);
        var settings = new PropertyHistoryDialogSettings(TimeSpan.FromHours(6), period, HistoryAggregations.Maximum);

        // Act
        var selection = PropertyHistoryChartModel.GetInitialSelection(settings, NumberAggregations, isDiscrete: false);

        // Assert
        Assert.Equal("Last 6 hours", PropertyHistoryChartModel.RangePresets[selection.RangeIndex].Label);
        Assert.Equal("10m", PropertyHistoryChartModel.Periods[selection.PeriodIndex].ShortName);
        Assert.Equal(HistoryAggregations.Maximum, selection.Aggregation);
    }

    [Theory]
    [InlineData("Auto")]
    [InlineData("Raw")]
    public void WhenTheDialogOpensWithAnAutoOrRawPeriod_ThenItSelectsThatPeriod(string shortName)
    {
        // Arrange
        PropertyHistoryChartModel.TryGetPeriod(shortName, out var period);

        // Act
        var selection = PropertyHistoryChartModel.GetInitialSelection(
            new PropertyHistoryDialogSettings(Period: period), NumberAggregations, isDiscrete: false);

        // Assert
        Assert.Equal(period, PropertyHistoryChartModel.Periods[selection.PeriodIndex]);
    }

    [Theory]
    [InlineData(30, "Last hour")]
    [InlineData(3 * 60, "Last 6 hours")]
    [InlineData(60 * 24 * 60, "Last 30 days")]
    public void WhenTheDialogOpensWithARangeThatIsNoPreset_ThenItSelectsTheShortestPresetAtLeastAsLongOrTheLongest(
        int rangeMinutes, string expectedLabel)
    {
        // Arrange
        var settings = new PropertyHistoryDialogSettings(TimeSpan.FromMinutes(rangeMinutes));

        // Act
        var selection = PropertyHistoryChartModel.GetInitialSelection(settings, NumberAggregations, isDiscrete: false);

        // Assert
        Assert.Equal(expectedLabel, PropertyHistoryChartModel.RangePresets[selection.RangeIndex].Label);
    }

    [Fact]
    public void WhenTheDialogOfACounterOpensWithSum_ThenItSelectsItsDefault()
    {
        // Arrange
        var offered = PropertyHistoryChartModel.GateAggregations(
            HistoryColumns.GetValueColumnFor(typeof(double)), isCumulative: true, new HashSet<string>(HistoryAggregations.All));
        var settings = new PropertyHistoryDialogSettings(Aggregation: HistoryAggregations.Sum);

        // Act
        var selection = PropertyHistoryChartModel.GetInitialSelection(settings, offered, isDiscrete: false);

        // Assert
        Assert.Equal(HistoryAggregations.Last, selection.Aggregation);
    }
}
