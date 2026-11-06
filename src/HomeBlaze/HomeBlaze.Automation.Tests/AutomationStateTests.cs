using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Tests.Testing;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationStateTests
{
    private static Action<Automation> WashingMachine(bool withEntryRule = false) => automation =>
    {
        automation.States = ["Idle", "Running"];
        automation.InitialState = "Idle";
        automation.RuleOrder = ["started", "finished"];
        var rules = Keyed(
            ("started", Create("Started", AutomationRuleMode.On, [When("/Weather/Power > 500")], [Enter("Running")], inState: "Idle")),
            ("finished", Create(
                "Finished",
                AutomationRuleMode.On,
                [When("/Weather/Power < 5", TimeSpan.FromMinutes(10))],
                [Invoke("/Notifier/SendNotification", ("notification.Message", "Washing machine finished")), Enter("Idle")],
                inState: "Running")));
        if (withEntryRule)
        {
            rules["entered"] = Create(
                "Entered",
                AutomationRuleMode.On,
                [OnEvent("..", "AutomationStateChangedEvent")],
                [Set("/Weather/Name", "{{ event/PreviousState }} to {{ event/NewState }}")]);
        }

        automation.Rules = rules;
    };

    private static async Task SetPowerAsync(AutomationTestHost host, double power)
    {
        host.Weather.Power = power;
        await host.FlushAsync();
    }

    private static async Task AdvanceAsync(AutomationTestHost host, TimeSpan time)
    {
        host.Time.Advance(time);
        await host.FlushAsync();
    }

    [Fact]
    public async Task WhenACycleEnds_ThenOneNotificationIsSentAndThePumpPhaseIsIgnored()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(WashingMachine());
        await SetPowerAsync(host, 600);
        var stateWhileWashing = host.Automation.CurrentState;

        // Act
        await SetPowerAsync(host, 3);
        await AdvanceAsync(host, TimeSpan.FromMinutes(5));
        await SetPowerAsync(host, 300);
        await SetPowerAsync(host, 3);
        await AdvanceAsync(host, TimeSpan.FromMinutes(9));
        var notificationsBeforeTheHold = host.Notifier.Sent.Count;
        await AdvanceAsync(host, TimeSpan.FromMinutes(1));

        // Assert
        Assert.Equal("Running", stateWhileWashing);
        Assert.Equal(0, notificationsBeforeTheHold);
        Assert.Equal("Washing machine finished", Assert.Single(host.Notifier.Sent).Message);
        Assert.Equal("Idle", host.Automation.CurrentState);
    }

    [Fact]
    public async Task WhenTheMachineIsOnStandbyAfterARestartInIdle_ThenNothingIsSent()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(WashingMachine());
        await host.RestartAsync();

        // Act
        await SetPowerAsync(host, 2);
        await AdvanceAsync(host, TimeSpan.FromMinutes(15));

        // Assert
        Assert.Empty(host.Notifier.Sent);
        Assert.Equal("Idle", host.Automation.CurrentState);
    }

    [Fact]
    public async Task WhenTheStateChanges_ThenARuleOnTheStateChangeEventRunsWithThePreviousAndTheNewState()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(WashingMachine(withEntryRule: true));

        // Act
        await SetPowerAsync(host, 600);

        // Assert
        Assert.Equal("Idle to Running", host.Weather.Name);
        Assert.Equal(1L, host.Rule("entered").RunCount);
        Assert.Equal(AutomationTestHost.Start, host.Automation.StateEnteredAt);
    }

    [Fact]
    public async Task WhenTheStateChanges_ThenAnotherAutomationCanReactToIt()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(WashingMachine());
        var washer = host.Automation;
        var watcher = await host.AddAutomationAsync("Watcher", automation => automation.Rules = Keyed(
            ("follow", On("Follow", OnEvent("/Storm", "AutomationStateChangedEvent"), Set("/Weather/Name", "{{ event/NewState }}")))));

        // Act
        host.Weather.Power = 600;
        await washer.FlushAsync();
        await watcher.FlushAsync();

        // Assert
        Assert.Equal("Running", host.Weather.Name);
    }

    [Fact]
    public async Task WhenTheStateIsReset_ThenTheInitialStateIsEnteredAndTheTriggerTimesAreCleared()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(WashingMachine(withEntryRule: true));
        await SetPowerAsync(host, 600);
        await SetPowerAsync(host, 0);
        var triggeredBeforeReset = host.Rule("started").LastTriggeredAt;

        // Act
        host.Automation.ResetState();
        await host.FlushAsync();
        var nameAfterReset = host.Weather.Name;
        await host.RestartAsync();

        // Assert
        Assert.NotNull(triggeredBeforeReset);
        Assert.Equal("Running to Idle", nameAfterReset);
        Assert.Equal("Idle", host.Automation.CurrentState);
        Assert.Null(host.Rule("started").LastTriggeredAt);
    }

    [Fact]
    public async Task WhenTheSavedStateIsNoLongerAStateAndTheStateIsReset_ThenTheInitialStateIsEnteredAndTheErrorIsCleared()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(WashingMachine());
        await SetPowerAsync(host, 600);
        host.Automation.States = ["Idle"];
        host.Automation.RuleOrder = [];
        host.Automation.Rules = new Dictionary<string, AutomationRule>();
        await host.Automation.ApplyConfigurationAsync(CancellationToken.None);
        await host.FlushAsync();
        var messageBeforeReset = host.Automation.StatusMessage;

        // Act
        host.Automation.ResetState();
        await host.FlushAsync();

        // Assert
        Assert.StartsWith("The saved state 'Running' is not a state of the automation", messageBeforeReset);
        Assert.Equal("Idle", host.Automation.CurrentState);
        Assert.Equal(ServiceStatus.Running, host.Automation.Status);
        Assert.Null(host.Automation.StatusMessage);
    }

    [Fact]
    public async Task WhenTheResetCannotBeSaved_ThenTheStateAndTheTriggerTimesStayAndTheErrorIsShown()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(WashingMachine());
        await SetPowerAsync(host, 600);
        File.Delete(host.CheckpointPath);
        Directory.CreateDirectory(host.CheckpointPath);

        // Act
        host.Automation.ResetState();
        await host.FlushAsync();

        // Assert
        Assert.Equal("Running", host.Automation.CurrentState);
        Assert.NotNull(host.Rule("started").LastTriggeredAt);
        Assert.Equal(ServiceStatus.Error, host.Automation.Status);
        Assert.StartsWith("The reset was not saved, so nothing changed", host.Automation.StatusMessage);
    }

    [Fact]
    public async Task WhenTheAutomationIsStopped_ThenAResetIsRefused()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(WashingMachine());
        await host.StopAsync();

        // Act
        var exception = Record.Exception(host.Automation.ResetState);

        // Assert
        Assert.Equal("The automation is not running.", Assert.IsType<InvalidOperationException>(exception).Message);
    }

    [Fact]
    public async Task WhenTheStateCannotBeSaved_ThenTheStateStaysAndNoEventIsPublished()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(WashingMachine(withEntryRule: true));
        File.Delete(host.CheckpointPath);
        Directory.CreateDirectory(host.CheckpointPath);

        // Act
        await SetPowerAsync(host, 600);

        // Assert
        Assert.Equal("Idle", host.Automation.CurrentState);
        Assert.Null(host.Weather.Name);
        Assert.Equal(AutomationRunOutcome.CompletedWithErrors, host.Rule("started").LastRunOutcome);
        Assert.Contains("The state Running was not entered because it could not be saved", host.Rule("started").LastRunMessage);
        Assert.Equal(ServiceStatus.Error, host.Automation.Status);
        Assert.StartsWith("Rule 'Started': the run was not saved", host.Automation.StatusMessage);
    }
}
