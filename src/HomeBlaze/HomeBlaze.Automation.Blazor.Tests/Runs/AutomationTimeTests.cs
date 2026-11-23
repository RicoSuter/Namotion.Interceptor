using System.Globalization;
using HomeBlaze.Automation.Blazor.Runs;
using HomeBlaze.Components.Abstractions.TimeZones;

namespace HomeBlaze.Automation.Blazor.Tests.Runs;

public class AutomationTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 30, 0, TimeSpan.Zero);

    private static TimeZoneDisplayService CreateResolved()
    {
        var timeZone = new TimeZoneDisplayService();
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test +2", "Test +2");
        timeZone.SetResolved(TimeZonePreference.Specific("Test+2"), zone);
        return timeZone;
    }

    [Fact]
    public void WhenTheTimeIsOnTheCurrentDayOfTheViewer_ThenOnlyTheTimeIsShown()
    {
        // Arrange
        var timeZone = CreateResolved();

        // Act
        var text = AutomationTime.Format(timeZone, Now.AddHours(-1), Now);

        // Assert
        Assert.Equal("13:30", text);
    }

    [Fact]
    public void WhenTheTimeIsOnAnotherDayOfTheViewer_ThenTheDateAndTheTimeAreShown()
    {
        // Arrange
        var timeZone = CreateResolved();
        var previousDay = Now.AddDays(-1);

        // Act
        var text = AutomationTime.Format(timeZone, previousDay, Now);

        // Assert
        Assert.Equal(new DateTime(2026, 11, 5, 14, 30, 0, DateTimeKind.Unspecified).ToString("g", CultureInfo.CurrentCulture), text);
    }

    [Fact]
    public void WhenTheTimeIsShownWithSeconds_ThenTheyAreShownOnTheCurrentDayAndOnAnotherDay()
    {
        // Arrange
        var timeZone = CreateResolved();
        var value = Now.AddHours(-1).AddSeconds(7);

        // Act
        var today = AutomationTime.FormatWithSeconds(timeZone, value, Now);
        var otherDay = AutomationTime.FormatWithSeconds(timeZone, value, Now.AddDays(1));

        // Assert
        Assert.Equal("13:30:07", today);
        Assert.Equal(new DateTime(2026, 11, 6, 13, 30, 7, DateTimeKind.Unspecified).ToString("G", CultureInfo.CurrentCulture), otherDay);
    }

    [Fact]
    public void WhenTheZoneIsNotResolved_ThenThePlaceholderIsShown()
    {
        // Arrange
        var timeZone = new TimeZoneDisplayService();

        // Act
        var text = AutomationTime.Format(timeZone, Now, Now);

        // Assert
        Assert.Equal(timeZone.Placeholder, text);
    }
}
