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

    [Theory]
    [InlineData(10, 27, 12, 25, 25)] // summer time ends on 25 October 2026 at 03:00
    [InlineData(10, 27, 23, 25, 25)]
    [InlineData(10, 26, 0, 25, 25)]
    [InlineData(3, 31, 12, 29, 23)] // summer time starts on 29 March 2026 at 02:00
    [InlineData(3, 31, 0, 29, 23)]
    [InlineData(3, 30, 23, 29, 23)]
    public void WhenTheClocksChangeInTheWindow_ThenItStillHoldsSevenDaysAndThatDayIsOneBucket(
        int month, int day, int hour, int dayOfChange, int hoursOfThatDay)
    {
        // Arrange
        var to = Local(2026, month, day, hour).AddMinutes(30);

        // Act
        var from = PropertyHistoryChartModel.GetWindowStart(to, TimeSpan.FromDays(7), Day);
        var starts = PropertyHistoryChartModel.GetBucketStarts(from, to, Day);

        // Assert
        Assert.Equal(7, starts.Count);
        Assert.Equal(Local(2026, month, day), starts[^1]);
        Assert.All(starts, start => Assert.Equal(TimeSpan.Zero, TimeZoneInfo.ConvertTime(start, Zurich).TimeOfDay));
        var change = starts.ToList().IndexOf(Local(2026, month, dayOfChange));
        Assert.Equal(TimeSpan.FromHours(hoursOfThatDay), starts[change + 1] - starts[change]);
    }

    [Theory]
    [InlineData(30, 23, 30)]
    [InlineData(1, 0, 1)]
    public void WhenTheRangeIsInDays_ThenTheWindowHoldsThatManyDaysAtAnyTimeOfDay(int rangeDays, int hour, int expectedDays)
    {
        // Arrange
        var to = Local(2026, 11, 10, hour).AddMinutes(30);

        // Act
        var from = PropertyHistoryChartModel.GetWindowStart(to, TimeSpan.FromDays(rangeDays), Day);

        // Assert
        Assert.Equal(expectedDays, PropertyHistoryChartModel.GetBucketStarts(from, to, Day).Count);
    }

    [Theory]
    [InlineData(2026, 10, 19, 2025, 11)]
    [InlineData(2028, 2, 29, 2027, 3)]
    [InlineData(2028, 3, 31, 2027, 4)]
    public void WhenTheBucketIsAMonthOverAYear_ThenTheWindowHoldsTwelveMonthsEndingWithThisOne(
        int year, int month, int day, int firstYear, int firstMonth)
    {
        // Arrange
        var bucket = HistoryBucket.Calendar(HistoryCalendarUnit.Month, Zurich);
        var to = Local(year, month, day, 9);

        // Act
        var from = PropertyHistoryChartModel.GetWindowStart(to, TimeSpan.FromDays(365), bucket);
        var starts = PropertyHistoryChartModel.GetBucketStarts(from, to, bucket);

        // Assert
        Assert.Equal(Local(firstYear, firstMonth, 1), from);
        Assert.Equal(12, starts.Count);
        Assert.Equal(Local(year, month, 1), starts[^1]);
    }

    [Theory]
    [InlineData(19)] // a Monday
    [InlineData(18)] // a Sunday
    public void WhenTheBucketIsAWeek_ThenNinetyDaysAreThirteenWeeksFromAMonday(int day)
    {
        // Arrange
        var week = HistoryBucket.Calendar(HistoryCalendarUnit.Week, Zurich);
        var to = Local(2026, 10, day, 9);

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
    public void WhenAnAutoPeriodResolvedToACalendarPeriod_ThenItsDescriptionNamesThatPeriod()
    {
        // Act
        var description = PropertyHistoryChartModel.DescribePeriod(PropertyHistoryChartModel.Periods[0], Day);

        // Assert
        Assert.Equal("calendar days", description);
    }

    [Theory]
    [InlineData("HH:mm", "MM-dd HH:mm")]
    [InlineData("ddd HH:mm", "MM-dd HH:mm")]
    [InlineData("HH:mm:ss", "MM-dd HH:mm:ss")]
    [InlineData("MM-dd", "yyyy-MM-dd")]
    [InlineData("MM-dd HH:mm", "yyyy-MM-dd HH:mm")]
    [InlineData("yyyy-MM", null)]
    [InlineData("yyyy-MM-dd HH:mm", null)]
    public void WhenALabelFormatRepeats_ThenTheLongerFormatTellsMoreApart(string format, string? expected)
    {
        // Act
        var longer = HistoryChartAxis.GetLongerTimeLabelFormat(format);

        // Assert
        Assert.Equal(expected, longer);
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
