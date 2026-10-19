using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Blazor;
using Xunit;

namespace HomeBlaze.History.Blazor.Tests;

public class HistoryChartCalendarBucketTests
{
    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    private static readonly HistoryBucket Day = HistoryBucket.Calendar(HistoryCalendarUnit.Day, Zurich);

    // A local time in Zurich as the instant it is.
    private static DateTimeOffset Local(int year, int month, int day, int hour = 0) =>
        BucketAlignment.LocalTimeToInstant(new DateTime(year, month, day, hour, 0, 0), Zurich);

    [Theory]
    [InlineData("day", HistoryCalendarUnit.Day)]
    [InlineData("WEEK", HistoryCalendarUnit.Week)]
    [InlineData("Month", HistoryCalendarUnit.Month)]
    public void WhenACalendarPeriodIsResolved_ThenItIsACalendarBucketInTheTimeZoneOfTheViewer(string shortName, HistoryCalendarUnit unit)
    {
        // Arrange
        Assert.True(PropertyHistoryChartModel.TryGetPeriod(shortName, out var period));

        // Act
        var bucket = PropertyHistoryChartModel.ResolveBucket(period, TimeSpan.FromDays(30), Zurich);

        // Assert
        Assert.False(period.IsRaw);
        Assert.False(period.IsAuto);
        Assert.Equal(HistoryBucket.Calendar(unit, Zurich), bucket);
    }

    [Fact]
    public void WhenTheBucketIsADay_ThenTheWindowStartsAtLocalMidnightAndHoldsTodayAndTheDaysBefore()
    {
        // Arrange - noon in summer time, two hours ahead of UTC.
        var to = Local(2026, 7, 15, 12);

        // Act
        var from = PropertyHistoryChartModel.GetWindowStart(to, TimeSpan.FromDays(7), Day);
        var starts = PropertyHistoryChartModel.GetBucketStarts(from, to, Day);

        // Assert
        Assert.Equal(Local(2026, 7, 9), from);
        Assert.Equal(new DateTimeOffset(2026, 7, 8, 22, 0, 0, TimeSpan.Zero), from);
        Assert.Equal(7, starts.Count);
        Assert.Equal(Local(2026, 7, 15), starts[^1]);
    }

    [Fact]
    public void WhenTheEndIsAtLocalMidnight_ThenTheWindowHoldsTheWholeDaysBefore()
    {
        // Arrange
        var to = Local(2026, 7, 15);

        // Act
        var from = PropertyHistoryChartModel.GetWindowStart(to, TimeSpan.FromDays(7), Day);

        // Assert
        Assert.Equal(Local(2026, 7, 8), from);
        Assert.Equal(7, PropertyHistoryChartModel.GetBucketStarts(from, to, Day).Count);
    }

    [Fact]
    public void WhenTheClocksGoBackInTheWindow_ThenThatDayIsOneBucketOf25Hours()
    {
        // Arrange - summer time ends on 25 October 2026 at 03:00 local time.
        var to = Local(2026, 10, 27, 12);

        // Act
        var from = PropertyHistoryChartModel.GetWindowStart(to, TimeSpan.FromDays(7), Day);
        var starts = PropertyHistoryChartModel.GetBucketStarts(from, to, Day);

        // Assert
        Assert.Equal(Local(2026, 10, 21), from);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 22, 0, 0, TimeSpan.Zero), from);
        Assert.Equal(7, starts.Count);
        var dayOfChange = starts.ToList().IndexOf(Local(2026, 10, 25));
        Assert.Equal(TimeSpan.FromHours(25), starts[dayOfChange + 1] - starts[dayOfChange]);
        Assert.All(starts, start => Assert.Equal(TimeSpan.Zero, TimeZoneInfo.ConvertTime(start, Zurich).TimeOfDay));
    }

    [Fact]
    public void WhenTheClocksGoForwardInTheWindow_ThenThatDayIsOneBucketOf23Hours()
    {
        // Arrange - summer time starts on 29 March 2026 at 02:00 local time.
        var to = Local(2026, 3, 31, 12);

        // Act
        var from = PropertyHistoryChartModel.GetWindowStart(to, TimeSpan.FromDays(7), Day);
        var starts = PropertyHistoryChartModel.GetBucketStarts(from, to, Day);

        // Assert
        Assert.Equal(Local(2026, 3, 25), from);
        Assert.Equal(7, starts.Count);
        var dayOfChange = starts.ToList().IndexOf(Local(2026, 3, 29));
        Assert.Equal(TimeSpan.FromHours(23), starts[dayOfChange + 1] - starts[dayOfChange]);
    }

    [Fact]
    public void WhenTheBucketIsAMonthOverAYear_ThenTheWindowHoldsTwelveMonthsEndingWithThisOne()
    {
        // Arrange
        var month = HistoryBucket.Calendar(HistoryCalendarUnit.Month, Zurich);
        var to = Local(2026, 10, 19, 9);

        // Act
        var from = PropertyHistoryChartModel.GetWindowStart(to, TimeSpan.FromDays(365), month);
        var starts = PropertyHistoryChartModel.GetBucketStarts(from, to, month);

        // Assert
        Assert.Equal(Local(2025, 11, 1), from);
        Assert.Equal(12, starts.Count);
        Assert.Equal(Local(2026, 10, 1), starts[^1]);
    }

    [Fact]
    public void WhenTheBucketIsAWeek_ThenTheWindowStartsOnAMonday()
    {
        // Arrange
        var week = HistoryBucket.Calendar(HistoryCalendarUnit.Week, Zurich);
        var to = Local(2026, 10, 19, 9);

        // Act
        var from = PropertyHistoryChartModel.GetWindowStart(to, TimeSpan.FromDays(90), week);

        // Assert
        var local = TimeZoneInfo.ConvertTime(from, Zurich);
        Assert.Equal(DayOfWeek.Monday, local.DayOfWeek);
        Assert.Equal(TimeSpan.Zero, local.TimeOfDay);
        Assert.Equal(13, PropertyHistoryChartModel.GetBucketStarts(from, to, week).Count);
    }

    [Fact]
    public void WhenTheRangeIsShorterThanTheBucket_ThenTheWindowHoldsTheCurrentPeriod()
    {
        // Arrange
        var to = Local(2026, 7, 15, 12);

        // Act
        var from = PropertyHistoryChartModel.GetWindowStart(to, TimeSpan.FromHours(1), Day);

        // Assert
        Assert.Equal(Local(2026, 7, 15), from);
    }

    [Fact]
    public void WhenTheBucketIsFixed_ThenTheWindowStartsTheRangeBeforeTheEnd()
    {
        // Arrange
        var to = Local(2026, 7, 15, 12);

        // Act
        var from = PropertyHistoryChartModel.GetWindowStart(to, TimeSpan.FromDays(7), TimeSpan.FromHours(24));

        // Assert
        Assert.Equal(to - TimeSpan.FromDays(7), from);
    }

    [Fact]
    public void WhenTheTimeZoneIsAnother_ThenTheDaysStartAtItsMidnight()
    {
        // Arrange
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var bucket = HistoryBucket.Calendar(HistoryCalendarUnit.Day, newYork);
        var to = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

        // Act
        var from = PropertyHistoryChartModel.GetWindowStart(to, TimeSpan.FromDays(2), bucket);

        // Assert - midnight in New York is 04:00 UTC in summer.
        Assert.Equal(new DateTimeOffset(2026, 7, 14, 4, 0, 0, TimeSpan.Zero), from);
    }

    [Theory]
    [InlineData("1y", "day")]
    [InlineData("1y", "week")]
    [InlineData("1y", "month")]
    [InlineData("30d", "day")]
    [InlineData("24h", "day")]
    public void WhenACalendarBucketIsUsed_ThenEveryRangeFitsThePointLimit(string range, string bucket)
    {
        // Act
        var error = HistoryChartSeriesResolver.FindArgumentError(range, bucket, null, 300, HistoryRefreshThrottle.MinimumInterval, []);

        // Assert
        Assert.Null(error);
    }

    [Fact]
    public void WhenAFixedBucketIsTooSmallForAYear_ThenTheErrorNamesTheSmallestThatFits()
    {
        // Act
        var error = HistoryChartSeriesResolver.FindArgumentError("1y", "1h", null, 300, HistoryRefreshThrottle.MinimumInterval, []);

        // Assert
        Assert.Equal("Bucket '1h' is too small for range '1y': use 12h or more, or Auto", error);
    }

    [Theory]
    [InlineData(HistoryCalendarUnit.Day, 1)]
    [InlineData(HistoryCalendarUnit.Week, 7)]
    [InlineData(HistoryCalendarUnit.Month, 31)]
    public void WhenTheNominalLengthOfACalendarBucketIsAsked_ThenItIsItsLongestLength(HistoryCalendarUnit unit, int days)
    {
        // Act
        var length = PropertyHistoryChartModel.GetNominalLength(HistoryBucket.Calendar(unit, Zurich));

        // Assert
        Assert.Equal(TimeSpan.FromDays(days), length);
    }

    [Theory]
    [InlineData("day", "calendar days")]
    [InlineData("week", "weeks from Monday")]
    [InlineData("month", "calendar months")]
    public void WhenACalendarPeriodIsDescribed_ThenItNamesItsUnit(string shortName, string expected)
    {
        // Arrange
        Assert.True(PropertyHistoryChartModel.TryGetPeriod(shortName, out var period));

        // Act
        var description = PropertyHistoryChartModel.DescribePeriod(period, HistoryBucket.Calendar(period.CalendarUnit!.Value, Zurich));

        // Assert
        Assert.Equal(expected, description);
    }

    [Fact]
    public void WhenTheBucketIsCalendar_ThenItDoesNotLengthenTheRefreshWindow()
    {
        // Act
        var window = HistoryRefreshThrottle.GetWindow(TimeSpan.FromMinutes(5), Day);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(5), window);
    }
}
