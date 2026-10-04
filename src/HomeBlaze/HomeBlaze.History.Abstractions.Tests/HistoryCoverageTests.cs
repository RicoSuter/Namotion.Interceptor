using System.Collections.Immutable;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Abstractions.Tests;

public class HistoryCoverageTests
{
    private static readonly DateTimeOffset Origin = new(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int minutes) => Origin.AddMinutes(minutes);

    [Fact]
    public void WhenRangesOverlapTheWindowPartly_ThenOnlyTheOverlapIsCounted()
    {
        // Arrange
        var ranges = ImmutableArray.Create(
            new HistoryCoverage(At(-5), At(3)),
            new HistoryCoverage(At(5), At(7)),
            new HistoryCoverage(At(9), At(20)));

        // Act
        var covered = HistoryCoverage.CoveredDuration(ranges, new HistoryCoverage(At(0), At(10)));

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(3 + 2 + 1), covered);
    }

    [Fact]
    public void WhenNoRangeOverlapsTheWindow_ThenNothingIsCovered()
    {
        // Arrange
        var ranges = ImmutableArray.Create(new HistoryCoverage(At(20), At(30)));

        // Act
        var covered = HistoryCoverage.CoveredDuration(ranges, new HistoryCoverage(At(0), At(10)));

        // Assert
        Assert.Equal(TimeSpan.Zero, covered);
    }

    [Fact]
    public void WhenOneRangeContainsTheWindow_ThenTheWholeWindowIsCovered()
    {
        // Arrange
        var ranges = ImmutableArray.Create(new HistoryCoverage(At(-100), At(100)));

        // Act
        var covered = HistoryCoverage.CoveredDuration(ranges, new HistoryCoverage(At(0), At(10)));

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(10), covered);
    }

    [Fact]
    public void WhenTheInstantIsARangeStart_ThenThatRangeStartIsReturned()
    {
        // Arrange
        var ranges = ImmutableArray.Create(new HistoryCoverage(At(0), At(5)), new HistoryCoverage(At(10), At(20)));

        // Act
        var start = HistoryCoverage.CoverageStartAt(ranges, At(10));

        // Assert
        Assert.Equal(At(10), start);
    }

    [Fact]
    public void WhenTheInstantIsARangeEnd_ThenItIsNotCovered()
    {
        // Arrange
        var ranges = ImmutableArray.Create(new HistoryCoverage(At(0), At(5)), new HistoryCoverage(At(10), At(20)));

        // Act
        var start = HistoryCoverage.CoverageStartAt(ranges, At(5));

        // Assert
        Assert.Null(start);
    }

    [Fact]
    public void WhenTheInstantIsJustBeforeARangeEnd_ThenThatRangeStartIsReturned()
    {
        // Arrange
        var ranges = ImmutableArray.Create(new HistoryCoverage(At(0), At(5)), new HistoryCoverage(At(10), At(20)));

        // Act
        var start = HistoryCoverage.CoverageStartAt(ranges, At(20).AddTicks(-1));

        // Assert
        Assert.Equal(At(10), start);
    }
}
