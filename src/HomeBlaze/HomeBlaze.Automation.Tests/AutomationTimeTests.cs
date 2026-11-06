using System.Globalization;
using HomeBlaze.Automation.Tests.Testing;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationTimeTests
{
    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    private static Action<Automation> DayAndNight() => automation =>
    {
        automation.States = ["Day", "Night"];
        automation.InitialState = "Night";
        automation.Rules = Keyed(
            ("morning", Create("Morning", AutomationRuleMode.On, [When("time >= 8h30m and time < 21h30m")], [Enter("Day")], inState: "Night")),
            ("evening", Create("Evening", AutomationRuleMode.On, [When("time >= 21h30m or time < 8h30m")], [Enter("Night")], inState: "Day")));
    };

    [Theory]
    [InlineData("2026-03-28T07:29:59Z")] // 08:29:59 in Zurich, the day before daylight saving time starts
    [InlineData("2026-03-29T06:29:59Z")] // 08:29:59 in Zurich, the day daylight saving time starts
    public async Task WhenTheLocalTimeReachesHalfPastEight_ThenTheDayBeginsWithinOneSecond(string start)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(DayAndNight(), DateTimeOffset.Parse(start, CultureInfo.InvariantCulture), Zurich);
        var stateBefore = host.Automation.CurrentState;

        // Act
        host.Time.Advance(TimeSpan.FromSeconds(1));
        await host.FlushAsync();

        // Assert
        Assert.Equal("Night", stateBefore);
        Assert.Equal("Day", host.Automation.CurrentState);
    }

    [Fact]
    public async Task WhenStartedInTheWrongPeriod_ThenTheStateIsReconciledAndTheNightBeginsAtHalfPastNine()
    {
        // Arrange
        var start = DateTimeOffset.Parse("2026-03-29T19:29:59Z", CultureInfo.InvariantCulture); // 21:29:59 in Zurich
        await using var host = await AutomationTestHost.StartAsync(DayAndNight(), start, Zurich);
        var stateAfterStart = host.Automation.CurrentState;

        // Act
        host.Time.Advance(TimeSpan.FromSeconds(1));
        await host.FlushAsync();

        // Assert
        Assert.Equal("Day", stateAfterStart);
        Assert.Equal("Night", host.Automation.CurrentState);
    }
}
