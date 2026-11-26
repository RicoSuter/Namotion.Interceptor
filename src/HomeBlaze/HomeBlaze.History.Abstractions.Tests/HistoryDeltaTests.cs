using HomeBlaze.History.Abstractions;
using Xunit;

namespace HomeBlaze.History.Abstractions.Tests;

public class HistoryDeltaTests
{
    [Theory]
    [InlineData(10, 15, 5)]
    [InlineData(10, 10, 0)]
    public void WhenTheCounterGrowsOrHolds_ThenTheStepIsTheIncrease(double previous, double current, double expected)
    {
        // Act
        var step = HistoryDelta.Step(previous, current);

        // Assert
        Assert.Equal(expected, step);
    }

    [Theory]
    [InlineData(1000, 3, 3)]
    [InlineData(1000, 0, 0)]
    [InlineData(1000, 499.9, 499.9)]
    public void WhenTheCounterDropsBelowHalf_ThenTheWholeValueCounts(double previous, double current, double expected)
    {
        // Act
        var step = HistoryDelta.Step(previous, current);

        // Assert
        Assert.Equal(expected, step);
    }

    [Theory]
    [InlineData(35_360_086, 35_360_074)]
    [InlineData(1000, 999)]
    [InlineData(1000, 500)]
    public void WhenTheCounterDropsToAtLeastHalf_ThenTheStepIsZero(double previous, double current)
    {
        // Act
        var step = HistoryDelta.Step(previous, current);

        // Assert
        Assert.Equal(0, step);
    }

    [Theory]
    [InlineData(0, -5, -5)]
    [InlineData(-10, -12, -12)]
    [InlineData(10, -5, -5)]
    public void WhenAReadingIsAtOrBelowZero_ThenADropCountsTheWholeReading(double previous, double current, double expected)
    {
        // Outside the cumulative convention: half of a reading at or below zero is never below it, so no drop is a step back.

        // Act
        var step = HistoryDelta.Step(previous, current);

        // Assert
        Assert.Equal(expected, step);
    }
}
