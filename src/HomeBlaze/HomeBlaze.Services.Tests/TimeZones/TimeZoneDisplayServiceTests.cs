using HomeBlaze.Services;
using Xunit;

namespace HomeBlaze.Services.Tests.TimeZones;

public class TimeZoneDisplayServiceTests
{
    private static TimeZoneInfo PlusTwo =>
        TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test +2", "Test +2");

    [Fact]
    public void WhenUnresolved_ThenFormatReturnsPlaceholder()
    {
        // Arrange
        var service = new TimeZoneDisplayService();

        // Act & Assert
        Assert.False(service.IsResolved);
        Assert.Equal(service.Placeholder, service.Format(DateTimeOffset.UtcNow));
        Assert.Equal(service.Placeholder, service.Format(DateTime.UtcNow));
    }

    [Fact]
    public void WhenResolved_ThenFormatConvertsToZoneWithOffset()
    {
        // Arrange
        var service = new TimeZoneDisplayService();
        service.SetResolved(TimeZonePreference.Specific("Test+2"), PlusTwo);

        // Act
        var result = service.Format(new DateTimeOffset(2026, 6, 26, 10, 0, 0, TimeSpan.Zero));

        // Assert
        // The offset is culture-robust (zzz always uses ':'); the 12:00 wall-clock value is
        // verified by WhenResolved_ThenToZonedReturnsWallClockInZone without culture sensitivity.
        Assert.Contains("+02:00", result);
    }

    [Fact]
    public void WhenResolved_ThenToZonedReturnsWallClockInZone()
    {
        // Arrange
        var service = new TimeZoneDisplayService();
        service.SetResolved(TimeZonePreference.Specific("Test+2"), PlusTwo);

        // Act
        var zoned = service.ToZoned(new DateTimeOffset(2026, 6, 26, 10, 0, 0, TimeSpan.Zero));

        // Assert
        Assert.Equal(new DateTime(2026, 6, 26, 12, 0, 0), zoned);
    }

    [Fact]
    public void WhenResolved_ThenToUtcInterpretsInputInZone()
    {
        // Arrange
        var service = new TimeZoneDisplayService();
        service.SetResolved(TimeZonePreference.Specific("Test+2"), PlusTwo);

        // Act
        var utc = service.ToUtc(new DateTime(2026, 6, 26, 12, 0, 0));

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 6, 26, 10, 0, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public void WhenResolved_ThenChangedEventFires()
    {
        // Arrange
        var service = new TimeZoneDisplayService();
        var raised = 0;
        service.Changed += () => raised++;

        // Act
        service.SetResolved(TimeZonePreference.Specific("Test+2"), PlusTwo);

        // Assert
        Assert.Equal(1, raised);
    }
}
