using HomeBlaze.History.Blazor;
using Xunit;

namespace HomeBlaze.History.Blazor.Tests;

public class HistoryChartAxisTests
{
    // As the chart counts its labels: from the step at or below the minimum to the step at or above the maximum.
    private static int CountLabels(double minimum, double maximum, HistoryChartValueAxis axis)
    {
        if (axis.IncludesZero)
        {
            minimum = Math.Min(minimum, 0);
            maximum = Math.Max(maximum, 0);
        }

        if (axis.SuggestedMaximum is { } suggested)
        {
            maximum = Math.Max(maximum, suggested);
        }

        return (int)(Math.Ceiling(maximum / axis.Step) - Math.Floor(minimum / axis.Step) + 1);
    }

    [Theory]
    [InlineData(300, 5)]
    [InlineData(184, 3)]
    [InlineData(600, 10)]
    [InlineData(80, 2)]
    [InlineData(0, 2)]
    [InlineData(double.NaN, 2)]
    public void WhenLinesHaveAHeight_ThenAboutOneValueLabelFitsPer55Pixels(double height, int expected)
    {
        // Act
        var count = HistoryChartAxis.GetValueLabelCount(height);

        // Assert
        Assert.Equal(expected, count);
    }

    [Theory]
    [InlineData(0, 1200, 300, 500)]
    [InlineData(0, 1200, 184, 1000)]
    [InlineData(1020, 1200, 300, 50)]
    [InlineData(720, 1200, 300, 200)]
    [InlineData(40.2, 46.7, 300, 2)]
    [InlineData(-15, 25, 300, 20)]
    [InlineData(-80, -20, 300, 20)]
    [InlineData(0, 2_500_000, 300, 1_000_000)]
    [InlineData(0, 1, 300, 1)]
    [InlineData(0.5, 0.501, 300, 1)]
    public void WhenValuesSpanARange_ThenTheStepIsTheSmallestNiceStepWhoseLabelsFit(
        double minimum, double maximum, double height, int expectedStep)
    {
        // Act
        var axis = HistoryChartAxis.GetValueAxis(minimum, maximum, height);

        // Assert
        Assert.Equal(expectedStep, axis.Step);
        Assert.False(axis.IncludesZero);
        Assert.Null(axis.SuggestedMaximum);
        Assert.InRange(CountLabels(minimum, maximum, axis), 2, HistoryChartAxis.GetValueLabelCount(height));
    }

    [Fact]
    public void WhenRangesGrow_ThenEveryStepIsOneTwoTwoAndAHalfOrFiveTimesAPowerOfTen()
    {
        // Arrange
        var steps = new SortedSet<int>();

        // Act
        for (var maximum = 1.0; maximum <= 4000; maximum *= 1.1)
        {
            steps.Add(HistoryChartAxis.GetValueAxis(0, maximum, 300).Step);
        }

        // Assert
        Assert.Equal([1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000], steps);
    }

    [Theory]
    [InlineData(1200)]
    [InlineData(45.6)]
    [InlineData(-30)]
    public void WhenTheValueIsConstant_ThenTheAxisReachesToZero(double value)
    {
        // Act
        var axis = HistoryChartAxis.GetValueAxis(value, value, 300);

        // Assert
        Assert.True(axis.IncludesZero);
        Assert.Null(axis.SuggestedMaximum);
        Assert.InRange(CountLabels(value, value, axis), 2, 5);
    }

    [Fact]
    public void WhenTheValueIsConstantlyZero_ThenTheAxisReachesToOne()
    {
        // Act
        var axis = HistoryChartAxis.GetValueAxis(0, 0, 300);

        // Assert
        Assert.Equal(new HistoryChartValueAxis(1, true, 1), axis);
        Assert.Equal(2, CountLabels(0, 0, axis));
    }

    [Fact]
    public void WhenTheRangeIsBeyondTheLargestStep_ThenTheStepIsTheLargestWholeStep()
    {
        // Act
        var axis = HistoryChartAxis.GetValueAxis(0, 1e15, 300);

        // Assert
        Assert.Equal(1_000_000_000, axis.Step);
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(0, double.PositiveInfinity)]
    public void WhenAValueIsNotFinite_ThenTheStepIsOne(double minimum, double maximum)
    {
        // Act
        var axis = HistoryChartAxis.GetValueAxis(minimum, maximum, 300);

        // Assert
        Assert.Equal(new HistoryChartValueAxis(1, false, null), axis);
    }

    [Fact]
    public void WhenMinimumIsAboveMaximum_ThenTheyAreSwapped()
    {
        // Act
        var axis = HistoryChartAxis.GetValueAxis(1200, 0, 300);

        // Assert
        Assert.Equal(500, axis.Step);
    }

    [Theory]
    [InlineData(1352, 8)]
    [InlineData(600, 5)]
    [InlineData(368, 3)]
    [InlineData(100, 1)]
    public void WhenLinesHaveAWidth_ThenTheRangeIsSplitIntoAsManyPartsAsTheWidthHoldsLabels(double width, int expectedParts)
    {
        // Act
        var spacing = HistoryChartAxis.GetTimeLabelSpacing(TimeSpan.FromHours(24), null, width);

        // Assert
        Assert.Equal(TimeSpan.FromHours(24) / expectedParts, spacing);
    }

    [Fact]
    public void WhenTheBucketIsLongerThanTheSpacing_ThenTheSpacingIsTheBucket()
    {
        // Act
        var spacing = HistoryChartAxis.GetTimeLabelSpacing(TimeSpan.FromHours(1), TimeSpan.FromHours(1), 1352);

        // Assert
        Assert.Equal(TimeSpan.FromHours(1), spacing);
    }

    [Fact]
    public void WhenTheRangeIsEmpty_ThenTheSpacingIsAMinute()
    {
        // Act
        var spacing = HistoryChartAxis.GetTimeLabelSpacing(TimeSpan.Zero, null, 1352);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(1), spacing);
    }

    [Fact]
    public void WhenLabelsShowDates_ThenTheSpacingIsWholeDays()
    {
        // Act
        var week = HistoryChartAxis.GetTimeLabelSpacing(TimeSpan.FromDays(7), null, 1352);
        var month = HistoryChartAxis.GetTimeLabelSpacing(TimeSpan.FromDays(30), null, 1352);

        // Assert
        Assert.Equal(TimeSpan.FromDays(1), week);
        Assert.Equal(TimeSpan.FromDays(4), month);
    }

    [Theory]
    [InlineData(5, "HH:mm:ss")]
    [InlineData(60, "HH:mm")]
    [InlineData(1439, "HH:mm")]
    [InlineData(1440, "ddd HH:mm")]
    [InlineData(2880, "ddd HH:mm")]
    [InlineData(10080, "MM-dd")]
    public void WhenRangeHasALength_ThenItsTimeLabelsTellEveryLabelApart(int minutes, string expected)
    {
        // Act
        var format = HistoryChartAxis.GetTimeLabelFormat(TimeSpan.FromMinutes(minutes));

        // Assert
        Assert.Equal(expected, format);
    }
}
