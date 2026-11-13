using HomeBlaze.History.Blazor;
using Xunit;

namespace HomeBlaze.History.Blazor.Tests;

public class HistoryChartPaletteTests
{
    private static readonly DateTimeOffset Origin = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double minutes) => Origin.AddMinutes(minutes);

    private static PropertyHistoryChartModel.StateSegment Segment(
        double fromMinutes, double toMinutes, string? label, bool isUnknown = false) =>
        new(At(fromMinutes), At(toMinutes), label, isUnknown);

    [Fact]
    public void WhenBooleanLabelsAreColored_ThenThePinnedPairTakesNoPaletteSlot()
    {
        // Arrange
        var palette = new HistoryStatePalette();

        // Act
        var yes = palette.GetColor("Yes");
        var no = palette.GetColor("No");
        var running = palette.GetColor("Running");

        // Assert
        Assert.Equal("#2979FF", yes);
        Assert.Equal("#9E9E9E", no);
        Assert.Equal("#00B8A9", running);
    }

    [Fact]
    public void WhenALabelIsColoredTwice_ThenItKeepsItsColorAndIsAssignedOnce()
    {
        // Arrange
        var palette = new HistoryStatePalette();

        // Act
        var first = palette.GetColor("Running");
        palette.GetColor("Stopped");
        var second = palette.GetColor("Running");

        // Assert
        Assert.Equal(first, second);
        Assert.Equal(["Running", "Stopped"], palette.Assigned.Select(entry => entry.Label));
    }

    [Fact]
    public void WhenSegmentsFillTheWindow_ThenTheBackgroundHasTwoHardStopsPerSegment()
    {
        // Arrange
        var palette = new HistoryStatePalette();
        var segments = new[] { Segment(0, 25, "Running"), Segment(25, 100, "Stopped") };

        // Act
        var background = HistoryChartPalette.BuildBandBackground(segments, At(0), At(100), palette);

        // Assert
        Assert.Equal("linear-gradient(to right, #00B8A9 0%, #00B8A9 25%, #F6A609 25%, #F6A609 100%)", background);
    }

    [Fact]
    public void WhenASegmentIsUnknown_ThenTheHatchIsLayeredBelowATransparentStop()
    {
        // Arrange
        var palette = new HistoryStatePalette();
        var segments = new[] { Segment(0, 50, null, isUnknown: true), Segment(50, 100, "Running") };

        // Act
        var background = HistoryChartPalette.BuildBandBackground(segments, At(0), At(100), palette);

        // Assert
        Assert.Equal(
            "linear-gradient(to right, transparent 0%, transparent 50%, #00B8A9 50%, #00B8A9 100%), " + HistoryChartPalette.UnknownFill,
            background);
    }

    [Fact]
    public void WhenTheWindowIsEmpty_ThenTheBackgroundIsTheHatch()
    {
        // Act
        var background = HistoryChartPalette.BuildBandBackground([], At(0), At(0), new HistoryStatePalette());

        // Assert
        Assert.Equal(HistoryChartPalette.UnknownFill, background);
    }

    [Fact]
    public void WhenABandHasGaps_ThenItsLegendListsEachOfItsStatesOnceAndEndsWithTheGaps()
    {
        // Arrange
        var palette = new HistoryStatePalette();
        PropertyHistoryChartModel.StateSegment[] segments =
            [Segment(0, 20, "Running"), Segment(20, 40, null, isUnknown: true), Segment(40, 60, "Stopped"), Segment(60, 80, null), Segment(80, 100, "Running")];
        HistoryChartPalette.BuildBandBackground(segments, At(0), At(100), palette);

        // Act
        var legend = HistoryChartPalette.BuildBandLegend(segments, palette);

        // Assert
        Assert.Equal(
            [
                new HistoryChartLegendEntry("Running", "#00B8A9"),
                new HistoryChartLegendEntry("Stopped", "#F6A609"),
                new HistoryChartLegendEntry(HistoryChartPalette.UnknownLabel, HistoryChartPalette.UnknownFill),
                new HistoryChartLegendEntry(HistoryChartPalette.NoValueLabel, HistoryChartPalette.NoValueFill)
            ],
            legend);
    }

    [Fact]
    public void WhenBandsShareAPalette_ThenTheLegendOfEachListsOnlyItsOwnStatesInPaletteOrder()
    {
        // Arrange
        var palette = new HistoryStatePalette();
        PropertyHistoryChartModel.StateSegment[] first = [Segment(0, 50, "Running"), Segment(50, 100, "Stopped")];
        PropertyHistoryChartModel.StateSegment[] second = [Segment(0, 50, "Error"), Segment(50, 100, "Running")];
        HistoryChartPalette.BuildBandBackground(first, At(0), At(100), palette);
        HistoryChartPalette.BuildBandBackground(second, At(0), At(100), palette);

        // Act
        var legend = HistoryChartPalette.BuildBandLegend(second, palette);

        // Assert
        Assert.Equal(
            [new HistoryChartLegendEntry("Running", "#00B8A9"), new HistoryChartLegendEntry("Error", "#8E44AD")],
            legend);
    }

    [Theory]
    [InlineData(true, null, "No data")]
    [InlineData(false, null, "(no value)")]
    [InlineData(false, "Running", "Running")]
    public void WhenASegmentIsDescribed_ThenItReadsAsItsStateOrItsGap(bool isUnknown, string? label, string expected)
    {
        // Act
        var text = HistoryChartPalette.DescribeSegment(Segment(0, 1, label, isUnknown));

        // Assert
        Assert.Equal(expected, text);
    }

    [Fact]
    public void WhenMoreLinesThanColorsAreDrawn_ThenTheLineColorsRepeat()
    {
        // Act
        var first = HistoryChartPalette.GetLineColor(0);
        var wrapped = HistoryChartPalette.GetLineColor(HistoryChartPalette.LineColors.Count);

        // Assert
        Assert.Equal("#2979FF", first);
        Assert.Equal(first, wrapped);
    }
}
