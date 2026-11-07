using System.Reactive;
using HomeBlaze.Abstractions.Sensors;
using HomeBlaze.Automation.Tests.Testing;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationRestartTests
{
    private static Action<Automation> DoorReminder() => automation =>
    {
        automation.States = ["Closed", "Open", "Informed"];
        automation.InitialState = "Closed";
        automation.RuleOrder = ["opened", "remind", "shut"];
        automation.Rules = Keyed(
            ("opened", Create("Opened", AutomationRuleMode.On, [When("/Weather/Door == Open")], [Enter("Open")], inState: "Closed")),
            ("remind", Create(
                "Remind",
                AutomationRuleMode.On,
                [When("now - ../StateEnteredAt > 10m and /Weather/Door == Open")],
                [Invoke("/Notifier/SendNotification", ("notification.Message", "Front door open since {{ ../StateEnteredAt | raw }}")), Enter("Informed")],
                inState: "Open")),
            ("shut", Create("Shut", AutomationRuleMode.On, [When("/Weather/Door == Closed")], [Enter("Closed")])));
    };

    private static async Task SetDoorAsync(AutomationTestHost host, DoorState door)
    {
        host.Weather.Door = door;
        await host.FlushAsync();
    }

    private static async Task AdvanceAsync(AutomationTestHost host, TimeSpan time)
    {
        host.Time.Advance(time);
        await host.FlushAsync();
    }

    [Fact]
    public async Task WhenTheDoorStaysOpen_ThenTheReminderIsSentOnceAfterTenMinutes()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(DoorReminder());
        await SetDoorAsync(host, DoorState.Open);

        // Act
        await AdvanceAsync(host, TimeSpan.FromMinutes(10));
        var sentAtTenMinutes = host.Notifier.Sent.Count;
        await AdvanceAsync(host, TimeSpan.FromSeconds(1));
        await AdvanceAsync(host, TimeSpan.FromMinutes(5));

        // Assert
        Assert.Equal(0, sentAtTenMinutes);
        Assert.Equal("Front door open since 2026-11-06T12:00:00.0000000+00:00", Assert.Single(host.Notifier.Sent).Message);
        Assert.Equal("Informed", host.Automation.CurrentState);
    }

    [Fact]
    public async Task WhenTheDoorClosesBeforeTheReminder_ThenTheStateReturnsToClosedAndNothingIsSent()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(DoorReminder());
        await SetDoorAsync(host, DoorState.Open);
        await AdvanceAsync(host, TimeSpan.FromMinutes(5));

        // Act
        await SetDoorAsync(host, DoorState.Closed);
        await AdvanceAsync(host, TimeSpan.FromMinutes(10));

        // Assert
        Assert.Equal("Closed", host.Automation.CurrentState);
        Assert.Empty(host.Notifier.Sent);
    }

    [Fact]
    public async Task WhenRestartedInAnOverdueOpenState_ThenTheReminderWaitsForTheFirstDoorReading()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(DoorReminder());
        await SetDoorAsync(host, DoorState.Open);
        await host.StopAsync();
        host.Time.Advance(TimeSpan.FromMinutes(15));

        // Act
        await host.RestartAsync();

        // Assert
        Assert.Equal("Open", host.Automation.CurrentState);
        Assert.Equal(AutomationTestHost.Start, host.Automation.StateEnteredAt);
        Assert.Equal("On: Unknown since 2026-11-06 12:15:00, missing /Weather/Door", host.Rule("remind").Status);
        Assert.Empty(host.Notifier.Sent);
    }

    [Fact]
    public async Task WhenRestartedInAnOverdueOpenStateAndTheDoorIsOpen_ThenTheReminderIsSentAtOnceWithTheOriginalDeadline()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(DoorReminder());
        await SetDoorAsync(host, DoorState.Open);
        await host.StopAsync();
        host.Time.Advance(TimeSpan.FromMinutes(15));
        await host.RestartAsync();

        // Act
        await SetDoorAsync(host, DoorState.Open);

        // Assert
        Assert.Equal("Front door open since 2026-11-06T12:00:00.0000000+00:00", Assert.Single(host.Notifier.Sent).Message);
        Assert.Equal("Informed", host.Automation.CurrentState);
        Assert.Equal(AutomationTestHost.Start.AddMinutes(15), host.Automation.StateEnteredAt);
    }

    [Fact]
    public async Task WhenRestoredInTheInformedState_ThenNoStateChangeIsPublishedAndNothingIsSentAgain()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(DoorReminder());
        await SetDoorAsync(host, DoorState.Open);
        await AdvanceAsync(host, TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        var stateChanges = new List<string>();
        using var subscription = host.Automation.Subscribe(Observer.Create<AutomationStateChangedEvent>(change => stateChanges.Add(change.NewState)));

        // Act
        await host.RestartAsync();
        await SetDoorAsync(host, DoorState.Open);

        // Assert
        Assert.Equal("Informed", host.Automation.CurrentState);
        Assert.Empty(stateChanges);
        Assert.Single(host.Notifier.Sent);
    }

    [Fact]
    public async Task WhenRestartedInAnOverdueOpenStateAndTheDoorIsClosed_ThenNothingIsSent()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(DoorReminder());
        await SetDoorAsync(host, DoorState.Open);
        await host.StopAsync();
        host.Time.Advance(TimeSpan.FromMinutes(15));
        await host.RestartAsync();

        // Act
        await SetDoorAsync(host, DoorState.Closed);

        // Assert
        Assert.Empty(host.Notifier.Sent);
        Assert.Equal("Closed", host.Automation.CurrentState);
    }

    [Fact]
    public async Task WhenRestartedWithinTheCooldown_ThenOnlyIfStillRejects()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("retract", Create(
                "Retract",
                AutomationRuleMode.On,
                [When("/Weather/Gust > 8")],
                [Invoke("/Blinds/Close")],
                onlyIf: "./LastTriggeredAt == null or now - ./LastTriggeredAt > 30m"))));
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Act
        await host.RestartAsync();
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(1L, host.Rule("retract").RunCount);
        Assert.Equal(AutomationRunOutcome.Succeeded, host.Rule("retract").LastRunOutcome);
    }
}
