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

    // As the chart bounds its axis: the lowest and the highest label.
    private static (double Lowest, double Highest) GetBounds(double minimum, double maximum, HistoryChartValueAxis axis)
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

        return (Math.Floor(minimum / axis.Step) * axis.Step, Math.Ceiling(maximum / axis.Step) * axis.Step);
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

    [Theory]
    [InlineData(0.23, 0.8, 300, 0, 100)]
    [InlineData(0.23, 0.8, 184, 0, 100)]
    [InlineData(0.5, 0.5, 300, 0, 100)]
    [InlineData(0.2, 1.2, 300, 0, 150)]
    [InlineData(-0.5, 0.8, 300, -50, 100)]
    public void WhenLinesAreInPercent_ThenTheAxisRunsFromZeroToHundredAndGrowsOnlyWhereTheValuesLeaveIt(
        double minimumFraction, double maximumFraction, double height, double expectedLowest, double expectedHighest)
    {
        // Arrange
        var minimum = minimumFraction * 100;
        var maximum = maximumFraction * 100;

        // Act
        var axis = HistoryChartAxis.GetPercentValueAxis(minimum, maximum, height);

        // Assert
        Assert.Equal((expectedLowest, expectedHighest), GetBounds(minimum, maximum, axis));
        Assert.InRange(CountLabels(minimum, maximum, axis), 2, HistoryChartAxis.GetValueLabelCount(height));
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
    [InlineData(1352, 9)]
    [InlineData(600, 6)]
    [InlineData(368, 4)]
    [InlineData(100, 2)]
    [InlineData(double.NaN, 2)]
    public void WhenLinesHaveAWidth_ThenAboutOneTimeLabelFitsPer90Pixels(double width, int expected)
    {
        // Act
        var count = HistoryChartAxis.GetTimeLabelCount(width);

        // Assert
        Assert.Equal(expected, count);
    }

    [Theory]
    [InlineData(1352, 3)]
    [InlineData(600, 6)]
    [InlineData(368, 12)]
    [InlineData(100, 12)]
    public void WhenADayIsSplitByTheWidth_ThenTheSpacingIsARoundStepThatFitsIt(double width, int expectedHours)
    {
        // Act
        var spacing = HistoryChartAxis.GetTimeLabelSpacing(TimeSpan.FromHours(24), null, width);

        // Assert
        Assert.Equal(TimeSpan.FromHours(expectedHours), spacing);
    }

    [Fact]
    public void WhenRangesAndWidthsVary_ThenEverySpacingIsARoundStepThatSplitsTheRangeIntoAFewParts()
    {
        // Arrange
        double[] widths = [100, 368, 600, 1128, 1352];

        // Act & Assert
        for (var range = TimeSpan.FromSeconds(30); range <= TimeSpan.FromDays(730); range *= 1.3)
        {
            foreach (var width in widths)
            {
                var spacing = HistoryChartAxis.GetTimeLabelSpacing(range, null, width);
                var parts = range / spacing;
                Assert.Contains(spacing, HistoryChartAxis.TimeLabelSteps);
                Assert.True(parts <= Math.Max(HistoryChartAxis.GetTimeLabelCount(width) - 1, 5), $"{range} at {width} px is split every {spacing}");
                Assert.True(parts >= 2 || range < TimeSpan.FromSeconds(2), $"{range} at {width} px is split every {spacing}");
            }
        }
    }

    [Theory]
    [InlineData(7, 3)]
    [InlineData(30, 14)]
    public void WhenTheNextRoundStepLeavesOneLabel_ThenTheLargestStepThatSplitsTheRangeInTwoIsTaken(int rangeDays, int expectedDays)
    {
        // Act
        var spacing = HistoryChartAxis.GetTimeLabelSpacing(TimeSpan.FromDays(rangeDays), null, 352);

        // Assert
        Assert.Equal(TimeSpan.FromDays(expectedDays), spacing);
    }

    [Theory]
    [InlineData(120, 30, 30)]
    [InlineData(1440, 240, 240)]
    public void WhenTheBucketIsLongerThanTheSpacing_ThenTheSpacingIsTheBucket(int rangeMinutes, int bucketMinutes, int expectedMinutes)
    {
        // Act
        var spacing = HistoryChartAxis.GetTimeLabelSpacing(
            TimeSpan.FromMinutes(rangeMinutes), TimeSpan.FromMinutes(bucketMinutes), 1352);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), spacing);
    }

    [Theory]
    [InlineData(60, 30)]
    [InlineData(60, 720)]
    public void WhenTheBucketIsAtLeastHalfTheRange_ThenTheSpacingIgnoresIt(int rangeMinutes, int bucketMinutes)
    {
        // Arrange
        var range = TimeSpan.FromMinutes(rangeMinutes);

        // Act
        var spacing = HistoryChartAxis.GetTimeLabelSpacing(range, TimeSpan.FromMinutes(bucketMinutes), 1352);

        // Assert
        Assert.Equal(HistoryChartAxis.GetTimeLabelSpacing(range, null, 1352), spacing);
        Assert.True(range / spacing >= 2, $"{range} is split every {spacing}");
    }

    [Fact]
    public void WhenTheRangeIsEmpty_ThenTheSpacingIsTheShortestStep()
    {
        // Act
        var spacing = HistoryChartAxis.GetTimeLabelSpacing(TimeSpan.Zero, null, 1352);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(1), spacing);
    }

    [Fact]
    public void WhenLabelsShowAWeekOrAMonth_ThenTheSpacingIsWholeDays()
    {
        // Act
        var week = HistoryChartAxis.GetTimeLabelSpacing(TimeSpan.FromDays(7), null, 1352);
        var month = HistoryChartAxis.GetTimeLabelSpacing(TimeSpan.FromDays(30), null, 1352);

        // Assert
        Assert.Equal(TimeSpan.FromDays(1), week);
        Assert.Equal(TimeSpan.FromDays(7), month);
    }

    [Theory]
    [InlineData(365, 400, 182)]
    [InlineData(365, 1352, 60)]
    [InlineData(730, 100, 365)]
    public void WhenTheRangeIsAYearOrLonger_ThenTheSpacingIsMonthsLong(int rangeDays, double width, int expectedDays)
    {
        // Act
        var spacing = HistoryChartAxis.GetTimeLabelSpacing(TimeSpan.FromDays(rangeDays), null, width);

        // Assert
        Assert.Equal(TimeSpan.FromDays(expectedDays), spacing);
    }

    [Theory]
    [InlineData(10, 0.5, "HH:mm:ss")]
    [InlineData(60, 10, "HH:mm")]
    [InlineData(1439, 180, "HH:mm")]
    [InlineData(1440, 180, "ddd HH:mm")]
    [InlineData(4320, 720, "ddd HH:mm")]
    [InlineData(10080, 1440, "MM-dd")]
    [InlineData(86400, 43200, "MM-dd")]
    [InlineData(525600, 86400, "yyyy-MM")]
    public void WhenRangeAndSpacingHaveALength_ThenTheTimeLabelsTellEveryLabelApart(int rangeMinutes, double spacingMinutes, string expected)
    {
        // Act
        var format = HistoryChartAxis.GetTimeLabelFormat(TimeSpan.FromMinutes(rangeMinutes), TimeSpan.FromMinutes(spacingMinutes));

        // Assert
        Assert.Equal(expected, format);
    }
}
