using System.Collections.Immutable;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Tests;

public sealed class HistoryDispatchPlannerTests
{
    private static readonly DateTimeOffset Origin = new(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int minutes) => Origin.AddMinutes(minutes);

    private static HistoryQuery TenMinuteQuery(int fromMinutes, int toMinutes) =>
        new("/Sensor/Temperature", At(fromMinutes), At(toMinutes), TimeSpan.FromMinutes(10),
            HistoryAggregations.Last, MaxPoints: 100);

    private static IReadOnlyList<PlannedSegment> Plan(HistoryQuery query, params FakeHistoryStore[] stores) =>
        HistoryDispatchPlanner.PlanBucketed(
            stores.OrderByDescending(store => store.Priority)
                .Select(store => new StoreCoverageSnapshot(store, store.CoverageRanges))
                .ToArray(),
            query);

    /// <summary>
    /// The carry-threaded executor serves every planned segment without budget accounting, which is
    /// only safe because the bucketed plan cannot exceed the point budget: PlanBucketed walks the grid
    /// from BucketAlignment.FirstBucketStart, already clipped to the newest MaxPoints buckets. If that
    /// ever stops holding, the executor would silently over-serve and blow the budget, so the
    /// invariant is asserted here rather than left as a comment.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    public void WhenTheRangeHasMoreBucketsThanTheBudget_ThenThePlanStaysWithinTheBudget(int maxPoints)
    {
        // Arrange - a day at one-minute buckets is 1440 buckets, split across two alternating owners
        // so the plan is many segments rather than one.
        var from = new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(1);
        var query = new HistoryQuery(
            "/Sensor/Temperature", from, to, TimeSpan.FromMinutes(1), HistoryAggregations.Last, maxPoints);

        var high = new FakeHistoryStore { Priority = 100, CoverageRanges = Alternating(from, to, offset: 0) };
        var low = new FakeHistoryStore { Priority = 50, CoverageRanges = Alternating(from, to, offset: 1) };

        // Act
        var segments = Plan(query, high, low);

        // Assert
        Assert.True(segments.Count > 0);
        Assert.True(segments.Sum(segment => segment.BucketCount) <= maxPoints);
    }

    [Fact]
    public void WhenALowerPriorityStoreCoversTheWholeBucket_ThenItWinsOverAPartialOne()
    {
        // Arrange
        var high = new FakeHistoryStore { Priority = 100, CurrentCoverage = new HistoryCoverage(At(5), At(10)) };
        var low = new FakeHistoryStore { Priority = 50, CurrentCoverage = new HistoryCoverage(At(0), At(10)) };

        // Act
        var segments = Plan(TenMinuteQuery(0, 10), high, low);

        // Assert
        Assert.Same(low, Assert.Single(segments).Store);
    }

    [Fact]
    public void WhenNoStoreCoversTheWholeBucket_ThenTheLargestOverlapWins()
    {
        // Arrange
        var high = new FakeHistoryStore { Priority = 100, CurrentCoverage = new HistoryCoverage(At(8), At(10)) };
        var low = new FakeHistoryStore { Priority = 50, CurrentCoverage = new HistoryCoverage(At(0), At(7)) };

        // Act
        var segments = Plan(TenMinuteQuery(0, 10), high, low);

        // Assert
        Assert.Same(low, Assert.Single(segments).Store);
    }

    [Fact]
    public void WhenPartialOverlapsAreEqual_ThenPriorityWins()
    {
        // Arrange
        var high = new FakeHistoryStore { Priority = 100, CurrentCoverage = new HistoryCoverage(At(1), At(5)) };
        var low = new FakeHistoryStore { Priority = 50, CurrentCoverage = new HistoryCoverage(At(6), At(10)) };

        // Act
        var segments = Plan(TenMinuteQuery(0, 10), high, low);

        // Assert
        Assert.Same(high, Assert.Single(segments).Store);
    }

    [Fact]
    public void WhenALargerPartialStartsInsideTheBucket_ThenTheStoreCoveringTheStartWins()
    {
        // Arrange - the live edge: a short-lived store covers the last six minutes, the persistent
        // store covers the bucket start up to its last flush.
        var live = new FakeHistoryStore { Priority = 100, CurrentCoverage = new HistoryCoverage(At(4), At(10)) };
        var persistent = new FakeHistoryStore { Priority = 50, CurrentCoverage = new HistoryCoverage(At(0), At(3)) };

        // Act
        var segments = Plan(TenMinuteQuery(0, 10), live, persistent);

        // Assert
        Assert.Same(persistent, Assert.Single(segments).Store);
    }

    [Fact]
    public void WhenTheOwnerHasAGapInsideABucket_ThenOneSegmentSpansItAndEndsInTheLaterRange()
    {
        // Arrange
        var store = new FakeHistoryStore
        {
            Priority = 100,
            CoverageRanges = [new HistoryCoverage(At(0), At(13)), new HistoryCoverage(At(16), At(30))]
        };

        // Act
        var segments = Plan(TenMinuteQuery(0, 30), store);

        // Assert
        var segment = Assert.Single(segments);
        Assert.Equal(At(0), segment.From);
        Assert.Equal(At(30), segment.To);
        Assert.Equal(3, segment.BucketCount);
        Assert.Equal(At(16), segment.EndCoverageFrom);
    }

    [Fact]
    public void WhenASegmentEndsUncovered_ThenEndCoverageFromIsNull()
    {
        // Arrange
        var store = new FakeHistoryStore { Priority = 100, CurrentCoverage = new HistoryCoverage(At(0), At(25)) };

        // Act
        var segments = Plan(TenMinuteQuery(0, 30), store);

        // Assert
        var segment = Assert.Single(segments);
        Assert.Equal(At(30), segment.To);
        Assert.Null(segment.EndCoverageFrom);
    }

    // Coverage over every other 10-minute slot, so neither store owns the whole range.
    private static ImmutableArray<HistoryCoverage> Alternating(DateTimeOffset from, DateTimeOffset to, int offset)
    {
        var slot = TimeSpan.FromMinutes(10);
        var builder = ImmutableArray.CreateBuilder<HistoryCoverage>();
        var index = 0;
        for (var start = from; start < to; start += slot, index++)
        {
            if (index % 2 == offset)
            {
                builder.Add(new HistoryCoverage(start, start + slot));
            }
        }

        return builder.ToImmutable();
    }
}
