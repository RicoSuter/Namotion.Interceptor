using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor.Testing;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationWakeUpTests
{
    private static async Task AdvanceAsync(AutomationTestHost host, TimeSpan time, TimeSpan step)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < time; elapsed += step)
        {
            host.Time.Advance(step);
            await host.FlushAsync();
        }
    }

    [Fact]
    public async Task WhenNoRuleReadsTheClockOrHolds_ThenTheAutomationNeverWakesUp()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")))));
        var wakeUpsAtStart = host.Clock.WakeUps;

        // Act
        await AdvanceAsync(host, TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));

        // Assert
        Assert.Equal(wakeUpsAtStart, host.Clock.WakeUps);
    }

    [Fact]
    public async Task WhenAHoldRuns_ThenTheRuleRunsWhenItEndsAndTheAutomationWakesUpAtMostOnceAMinute()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("retract", On("Retract", When("/Weather/Gust > 8", TimeSpan.FromMinutes(10)), Invoke("/Blinds/Close")))));
        host.Weather.Gust = 9;
        await host.FlushAsync();
        var wakeUpsAtStart = host.Clock.WakeUps;

        // Act
        await AdvanceAsync(host, TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(AutomationTestHost.Start.AddMinutes(10), host.Rule("retract").LastTriggeredAt);
        Assert.InRange(host.Clock.WakeUps - wakeUpsAtStart, 1, 12);
    }

    [Fact]
    public async Task WhenATriggerWaitsForATimeOfDay_ThenItFiresAtThatInstantAndTheAutomationWakesUpAtMostOnceAMinute()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("lunch", On("Lunch", When("time >= 13h"), Invoke("/Blinds/Close")))));
        var wakeUpsAtStart = host.Clock.WakeUps;

        // Act
        await AdvanceAsync(host, TimeSpan.FromHours(2), TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(AutomationTestHost.Start.AddHours(1), host.Rule("lunch").LastTriggeredAt);
        Assert.InRange(host.Clock.WakeUps - wakeUpsAtStart, 1, 125);
    }

    [Fact]
    public async Task WhenATriggerWaitsForTimeSinceAnInstant_ThenItFiresWhenTheTimePassed()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("stale", On("Stale", When("now - /Weather/LastUpdated > 5m"), Invoke("/Blinds/Close")))));
        host.Weather.LastUpdated = AutomationTestHost.Start;
        await host.FlushAsync();
        var wakeUpsAtStart = host.Clock.WakeUps;

        // Act
        await AdvanceAsync(host, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(AutomationTestHost.Start.AddMinutes(5).AddSeconds(1), host.Rule("stale").LastTriggeredAt);
        Assert.InRange(host.Clock.WakeUps - wakeUpsAtStart, 1, 12);
    }

    [Fact]
    public async Task WhenTheTimeAdvancesRightAfterAFlush_ThenATriggerThatWaitsForTimeSinceAnInstantFiresOnTime()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("stale", On("Stale", When("now - /Weather/LastUpdated > 5m"), Invoke("/Blinds/Close")))));
        host.Weather.LastUpdated = AutomationTestHost.Start;
        await host.FlushAsync();
        await AdvanceAsync(host, TimeSpan.FromSeconds(299), TimeSpan.FromSeconds(1));

        // Act
        host.Clock.PauseNextRead();
        await host.FlushAsync();
        host.Time.Advance(TimeSpan.FromSeconds(1));
        host.Clock.ResumeRead();
        await host.FlushAsync();
        await AdvanceAsync(host, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(AutomationTestHost.Start.AddMinutes(5).AddSeconds(1), host.Rule("stale").LastTriggeredAt);
    }

    [Fact]
    public async Task WhenTheWallClockIsSetForward_ThenATimeOfDayTriggerFiresWithinAMinute()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("lunch", On("Lunch", When("time >= 13h"), Invoke("/Blinds/Close")))));

        // Act
        host.Clock.WallClockShift = TimeSpan.FromMinutes(59);
        await AdvanceAsync(host, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenTheWallClockIsSetBack_ThenATimeOfDayTriggerSeesTheEarlierTimeWithinAMinute()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("lunch", On("Lunch", When("time >= 12h"), Invoke("/Blinds/Close")))));
        var statusAtStart = host.Rule("lunch").Status;

        // Act
        host.Clock.WallClockShift = TimeSpan.FromMinutes(-10);
        await AdvanceAsync(host, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal("On: Triggered, waiting to rearm", statusAtStart);
        Assert.Equal("On: Armed", host.Rule("lunch").Status);
    }

    [Fact]
    public async Task WhenAWakeUpIsDroppedBecauseTheQueueIsFull_ThenTheAutomationWakesUpAgainAfterwards()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("close", On("Close", When("/Weather/Gust > 8"), Invoke("/Blinds/Close"))),
            ("wait", On("Wait", When("/Weather/Rain > 0"), Invoke("/Blinds/Wait"))),
            ("hold", On("Hold", When("/Weather/Power > 0", TimeSpan.FromSeconds(30)), Invoke("/Blinds/Open")))));
        host.Weather.Gust = 9;
        host.Weather.Power = 1;
        await host.FlushAsync();
        host.Weather.Rain = 1;
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsWaiting);
        for (var rain = 2; rain <= AutomationInputQueue.Capacity + 10; rain++)
        {
            host.Weather.Rain = rain;
        }

        // Act
        host.Time.Advance(TimeSpan.FromSeconds(6));
        host.Blinds.Release();
        await host.FlushAsync();
        await AdvanceAsync(host, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(1, host.Blinds.OpenCount);
        Assert.True(File.Exists(host.CheckpointPath));
    }

    [Fact]
    public async Task WhenTheWallClockIsSetBackBeforeAnElapsedTimeThatHadPassed_ThenTheTriggerRearmsAndFiresAgainWhenItPasses()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("stale", On("Stale", When("now - /Weather/LastUpdated > 5m"), Invoke("/Blinds/Close")))));
        host.Weather.LastUpdated = AutomationTestHost.Start.AddMinutes(-10);
        await host.FlushAsync();
        var runsBeforeTheCorrection = host.Blinds.CloseCount;

        // Act
        host.Clock.WallClockShift = TimeSpan.FromMinutes(-20);
        await AdvanceAsync(host, TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(1, runsBeforeTheCorrection);
        Assert.Equal(2, host.Blinds.CloseCount);
    }
}
