using System.Collections.Immutable;
using HomeBlaze.History.Abstractions;
using Xunit;

namespace HomeBlaze.History.Mcp.Tests;

public class HistoryToolFormattingTests
{
    [Theory]
    [InlineData("2026-06-24T00:05:00Z", "2026-06-24T00:05:00Z")]
    [InlineData("2026-06-24T00:05:00.250Z", "2026-06-24T00:05:00.25Z")]
    [InlineData("2026-06-24T00:05:00.0000001Z", "2026-06-24T00:05:00.0000001Z")]
    [InlineData("2026-06-24T02:05:00+02:00", "2026-06-24T00:05:00Z")]
    public void WhenFormattingTimestamp_ThenUtcWithoutZeroFraction(string input, string expected)
    {
        // Arrange
        var value = DateTimeOffset.Parse(input, System.Globalization.CultureInfo.InvariantCulture);

        // Act
        var formatted = HistoryToolFormatting.FormatTimestamp(value);

        // Assert
        Assert.Equal(expected, formatted);
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(5_000_000, "<1s")]
    [InlineData(450_000_000, "45s")]
    [InlineData(80_400_000_000, "2h14m")]
    [InlineData(2_736_000_000_000, "3d4h")]
    [InlineData(867_000_000_000, "1d5m")]
    public void WhenFormattingDuration_ThenNonZeroUnitsAreListed(long ticks, string expected)
    {
        // Act
        var formatted = HistoryToolFormatting.FormatDuration(TimeSpan.FromTicks(ticks));

        // Assert
        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void WhenCoverageHasGaps_ThenSummaryCountsThemAndFindsTheLargest()
    {
        // Arrange
        var start = new DateTimeOffset(2026, 6, 24, 0, 0, 0, TimeSpan.Zero);
        ImmutableArray<HistoryCoverage> ranges =
        [
            new(start.AddMinutes(5), start.AddMinutes(20)),
            new(start.AddMinutes(30), start.AddMinutes(55))
        ];

        // Act
        var summary = HistoryToolFormatting.Summarize(ranges, start, start.AddHours(1));

        // Assert
        Assert.Equal(0.666, summary.Ratio);
        Assert.Equal(3, summary.Gaps);
        Assert.Equal(TimeSpan.FromMinutes(10), summary.LargestGap);
    }

    [Fact]
    public void WhenCoverageIsComplete_ThenRatioIsOneAndNoGaps()
    {
        // Arrange
        var start = new DateTimeOffset(2026, 6, 24, 0, 0, 0, TimeSpan.Zero);
        ImmutableArray<HistoryCoverage> ranges = [new(start, start.AddHours(1))];

        // Act
        var summary = HistoryToolFormatting.Summarize(ranges, start, start.AddHours(1));

        // Assert
        Assert.Equal(1, summary.Ratio);
        Assert.Equal(0, summary.Gaps);
        Assert.Equal(TimeSpan.Zero, summary.LargestGap);
    }

    [Fact]
    public void WhenCoverageMissesOneSecondOfADay_ThenRatioStaysBelowOne()
    {
        // Arrange
        var start = new DateTimeOffset(2026, 6, 24, 0, 0, 0, TimeSpan.Zero);
        ImmutableArray<HistoryCoverage> ranges = [new(start.AddSeconds(1), start.AddDays(1))];

        // Act
        var summary = HistoryToolFormatting.Summarize(ranges, start, start.AddDays(1));

        // Assert
        Assert.Equal(0.999, summary.Ratio);
        Assert.Equal(1, summary.Gaps);
    }

    [Theory]
    [InlineData("2025-12-31T23:00:00Z", "2026-01-01T00:00:00+01:00")]
    [InlineData("2026-06-30T22:00:00Z", "2026-07-01T00:00:00+02:00")]
    public void WhenFormattingInAZone_ThenLocalTimeWithOffset(string input, string expected)
    {
        // Arrange
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
        var value = DateTimeOffset.Parse(input, System.Globalization.CultureInfo.InvariantCulture);

        // Act
        var formatted = HistoryToolFormatting.FormatTimestamp(value, zone);

        // Assert
        Assert.Equal(expected, formatted);
    }
}
