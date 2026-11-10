using System.Globalization;

namespace HomeBlaze.Services.Tests;

public class LocalClockTests
{
    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    private static DateTimeOffset Parse(string instant) => DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("2026-10-25T00:00:00Z", "02:00:00")] // the first 02:00 in Zurich, daylight saving time
    [InlineData("2026-10-25T00:59:59Z", "02:59:59")]
    [InlineData("2026-10-25T01:00:00Z", "02:59:59.9999999")] // the second 02:00, after the clocks went back
    [InlineData("2026-10-25T01:30:00Z", "02:59:59.9999999")]
    [InlineData("2026-10-25T02:00:00Z", "03:00:00")]
    public void WhenTheClockFallsBack_ThenTheTimeOfDayDoesNotMoveBackwards(string instant, string expected)
    {
        // Act
        var timeOfDay = LocalClock.GetTimeOfDay(Parse(instant), Zurich);

        // Assert
        Assert.Equal(TimeSpan.Parse(expected, CultureInfo.InvariantCulture), timeOfDay);
    }

    [Theory]
    [InlineData("2026-03-28T12:00:00Z", "2026-03-29T12:00:00Z", "2026-03-29T01:00:00Z")] // the clocks go forward
    [InlineData("2026-10-24T12:00:00Z", "2026-10-25T12:00:00Z", "2026-10-25T01:00:00Z")] // the clocks go back
    [InlineData("2026-10-24T12:00:00Z", "2026-10-25T00:59:59Z", null)]
    public void WhenSearchingUpToALimit_ThenTheInstantOfTheOffsetChangeOrNullIsFound(string now, string limit, string? expected)
    {
        // Arrange
        var offset = Zurich.GetUtcOffset(Parse(now));

        // Act
        var change = LocalClock.GetOffsetChange(Zurich, offset, Parse(now), Parse(limit));

        // Assert
        Assert.Equal(expected is null ? null : Parse(expected), change);
    }
}
