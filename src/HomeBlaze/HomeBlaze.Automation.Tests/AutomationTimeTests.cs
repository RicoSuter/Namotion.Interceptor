using System.Globalization;
using System.Reactive;
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
        await host.AdvanceAsync(TimeSpan.FromSeconds(1));

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
        await host.AdvanceAsync(TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal("Day", stateAfterStart);
        Assert.Equal("Night", host.Automation.CurrentState);
    }

    [Fact]
    public async Task WhenRestartedWithASavedStateOfTheWrongPeriod_ThenTheStateIsReconciled()
    {
        // Arrange
        var start = DateTimeOffset.Parse("2026-03-29T19:00:00Z", CultureInfo.InvariantCulture); // 21:00 in Zurich
        await using var host = await AutomationTestHost.StartAsync(DayAndNight(), start, Zurich);
        var savedState = host.Automation.CurrentState;
        await host.StopAsync();
        host.Time.Advance(TimeSpan.FromHours(2));
        var stateChanges = new List<string>();
        using var subscription = host.Automation.Subscribe(Observer.Create<AutomationStateChangedEvent>(change => stateChanges.Add(change.NewState)));

        // Act
        await host.RestartAsync();

        // Assert
        Assert.Equal("Day", savedState);
        Assert.Equal("Night", host.Automation.CurrentState);
        Assert.Equal(["Night"], stateChanges.ToArray());
    }

    [Theory]
    [InlineData("2026-03-28T20:00:00Z", "2026-03-29T06:31:00Z", "03-28 21:30 Night", "03-29 08:30 Day")] // daylight saving time starts at 02:00
    [InlineData("2026-10-24T19:00:00Z", "2026-10-25T07:31:00Z", "10-24 21:30 Night", "10-25 08:30 Day")] // daylight saving time ends at 03:00
    public async Task WhenTheTimeRunsThroughANightWithAClockChange_ThenTheStateChangesOnceAtEachLocalTime(
        string start, string end, string expectedEvening, string expectedMorning)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(DayAndNight(), DateTimeOffset.Parse(start, CultureInfo.InvariantCulture), Zurich);
        var stateChanges = new List<string>();
        using var subscription = host.Automation.Subscribe(Observer.Create<AutomationStateChangedEvent>(change =>
            stateChanges.Add($"{TimeZoneInfo.ConvertTime(change.Timestamp, Zurich):MM-dd HH:mm} {change.NewState}")));
        var stateAtStart = host.Automation.CurrentState;

        // Act
        var endTime = DateTimeOffset.Parse(end, CultureInfo.InvariantCulture);
        while (host.Time.GetUtcNow() < endTime)
        {
            await host.AdvanceAsync(TimeSpan.FromMinutes(1));
        }

        // Assert
        Assert.Equal("Day", stateAtStart);
        Assert.Equal([expectedEvening, expectedMorning], stateChanges.ToArray());
        Assert.Equal("Day", host.Automation.CurrentState);
    }

    [Fact]
    public async Task WhenTheClockFallsBackOverADailyTime_ThenTheRuleRunsOnce()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(
            automation => automation.Rules = Keyed(("daily", On("Daily", When("time >= 2h30m"), Invoke("/Blinds/Close")))),
            DateTimeOffset.Parse("2026-10-24T22:00:00Z", CultureInfo.InvariantCulture), // midnight in Zurich, the day daylight saving time ends
            Zurich);

        // Act
        for (var minute = 0; minute < 6 * 60; minute++)
        {
            await host.AdvanceAsync(TimeSpan.FromMinutes(1));
        }

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T00:30:00Z", CultureInfo.InvariantCulture), host.Rule("daily").LastTriggeredAt);
    }

    [Theory]
    [InlineData("2026-03-28T23:00:00Z", "2026-03-29T01:00:00Z", "2026-03-30T00:30:00Z")] // 02:30 does not exist on the day daylight saving time starts
    [InlineData("2026-10-24T22:00:00Z", "2026-10-25T00:30:00Z", "2026-10-26T01:30:00Z")] // 02:30 happens twice on the day it ends
    public async Task WhenADailyTimeIsInTheHourOfAClockChange_ThenTheRuleRunsOncePerDay(string start, string expectedFirst, string expectedSecond)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(
            automation => automation.Rules = Keyed(("daily", On("Daily", When("time >= 2h30m"), Invoke("/Blinds/Close")))),
            DateTimeOffset.Parse(start, CultureInfo.InvariantCulture),
            Zurich);
        var runs = new List<DateTimeOffset>();

        // Act
        for (var minute = 0; minute < 2 * 24 * 60; minute++)
        {
            var closeCount = host.Blinds.CloseCount;
            await host.AdvanceAsync(TimeSpan.FromMinutes(1));
            if (host.Blinds.CloseCount != closeCount)
            {
                runs.Add(host.Time.GetUtcNow());
            }
        }

        // Assert
        Assert.Equal(
            [DateTimeOffset.Parse(expectedFirst, CultureInfo.InvariantCulture), DateTimeOffset.Parse(expectedSecond, CultureInfo.InvariantCulture)],
            runs);
    }
}
