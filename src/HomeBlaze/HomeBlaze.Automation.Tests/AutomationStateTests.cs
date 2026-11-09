using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor.Testing;
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


    [Fact]
    public async Task WhenACycleEnds_ThenOneNotificationIsSentAndThePumpPhaseIsIgnored()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(WashingMachine());
        await SetPowerAsync(host, 600);
        var stateWhileWashing = host.Automation.CurrentState;

        // Act
        await SetPowerAsync(host, 3);
        await host.AdvanceAsync(TimeSpan.FromMinutes(5));
        await SetPowerAsync(host, 300);
        await SetPowerAsync(host, 3);
        await host.AdvanceAsync(TimeSpan.FromMinutes(9));
        var notificationsBeforeTheHold = host.Notifier.Sent.Count;
        await host.AdvanceAsync(TimeSpan.FromMinutes(1));

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
        await host.AdvanceAsync(TimeSpan.FromMinutes(15));

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
    public async Task WhenARuleEntersAState_ThenARuleActiveInThatStateReceivesTheEntryEvent()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Idle", "Running"];
            automation.InitialState = "Idle";
            automation.RuleOrder = ["started", "onEntry", "onLeave"];
            automation.Rules = Keyed(
                ("started", Create("Started", AutomationRuleMode.On, [When("/Weather/Power > 500")], [Enter("Running")], inState: "Idle")),
                ("onEntry", Create(
                    "On entry",
                    AutomationRuleMode.On,
                    [OnEvent("..", "AutomationStateChangedEvent", "event/NewState == Running")],
                    [Invoke("/Blinds/Close")],
                    inState: "Running")),
                ("onLeave", Create(
                    "On leave",
                    AutomationRuleMode.On,
                    [OnEvent("..", "AutomationStateChangedEvent")],
                    [Invoke("/Blinds/Open")],
                    inState: "Idle")));
        });

        // Act
        await SetPowerAsync(host, 600);

        // Assert
        Assert.Equal("Running", host.Automation.CurrentState);
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(0, host.Blinds.OpenCount);
    }

    [Fact]
    public async Task WhenARuleChangesTheStateForAnInput_ThenTheLaterRulesOfThatInputDoNotRun()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Idle", "Running"];
            automation.InitialState = "Idle";
            automation.RuleOrder = ["started", "idleOnly"];
            automation.Rules = Keyed(
                ("started", Create("Started", AutomationRuleMode.On, [When("/Weather/Power > 500")], [Enter("Running")], inState: "Idle")),
                ("idleOnly", Create("Idle only", AutomationRuleMode.On, [When("/Weather/Power > 500")], [Invoke("/Blinds/Close")], inState: "Idle")));
        });

        // Act
        await SetPowerAsync(host, 600);

        // Assert
        Assert.Equal("Running", host.Automation.CurrentState);
        Assert.Equal(0, host.Blinds.CloseCount);
        Assert.Equal(0L, host.Rule("idleOnly").RunCount);
        Assert.Null(host.Rule("idleOnly").LastRunOutcome);
    }

    [Fact]
    public async Task WhenARuleChangesTheStateForAnEvent_ThenALaterRuleOfThatEventWithoutAStateStillRuns()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Off", "On"];
            automation.InitialState = "Off";
            automation.RuleOrder = ["toggle", "notify"];
            automation.Rules = Keyed(
                ("toggle", Create("Toggle", AutomationRuleMode.On, [OnEvent("/Button", "TestPressEvent")], [Enter("On")], inState: "Off")),
                ("notify", Create("Notify", AutomationRuleMode.On, [OnEvent("/Button", "TestPressEvent")], [Invoke("/Blinds/Close")])));
        });

        // Act
        host.Button.Press("Release");
        await host.FlushAsync();

        // Assert
        Assert.Equal("On", host.Automation.CurrentState);
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(1L, host.Rule("notify").RunCount);
    }

    [Fact]
    public async Task WhenARuleChangesTheStateForAnEvent_ThenALaterRuleOfThatEventInAStateDoesNotRun()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Off", "On"];
            automation.InitialState = "Off";
            automation.RuleOrder = ["toggle", "offOnly"];
            automation.Rules = Keyed(
                ("toggle", Create("Toggle", AutomationRuleMode.On, [OnEvent("/Button", "TestPressEvent")], [Enter("On")], inState: "Off")),
                ("offOnly", Create("Off only", AutomationRuleMode.On, [OnEvent("/Button", "TestPressEvent")], [Invoke("/Blinds/Close")], inState: "Off")));
        });

        // Act
        host.Button.Press("Release");
        await host.FlushAsync();

        // Assert
        Assert.Equal("On", host.Automation.CurrentState);
        Assert.Equal(0, host.Blinds.CloseCount);
        Assert.Equal(0L, host.Rule("offOnly").RunCount);
    }

    [Fact]
    public async Task WhenTheButtonIsPressedAgainWhileTheFirstPressRuns_ThenTheSecondPressIsHandledInTheNewState()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Off", "On"];
            automation.InitialState = "Off";
            automation.RuleOrder = ["switchOn", "switchOff"];
            automation.Rules = Keyed(
                ("switchOn", Create(
                    "Switch on",
                    AutomationRuleMode.On,
                    [OnEvent("/Button", "TestPressEvent", "event/Gesture == Release")],
                    [Invoke("/Blinds/Wait"), Set("/Blinds/Position", "1"), Enter("On")],
                    inState: "Off")),
                ("switchOff", Create(
                    "Switch off",
                    AutomationRuleMode.On,
                    [OnEvent("/Button", "TestPressEvent", "event/Gesture == Release")],
                    [Set("/Blinds/Position", "2"), Enter("Off")],
                    inState: "On")));
        });

        // Act
        host.Button.Press("Release");
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsWaiting);
        host.Button.Press("Release");
        host.Blinds.Release();
        await host.FlushAsync();

        // Assert
        Assert.Equal(2, host.Blinds.Position);
        Assert.Equal("Off", host.Automation.CurrentState);
        Assert.Equal(1L, host.Rule("switchOff").RunCount);
    }

    [Fact]
    public async Task WhenARuleBeforeTheChangingRuleIsSwitchedOn_ThenItHandlesTheEventBeforeTheStateChanges()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Idle", "Running"];
            automation.InitialState = "Idle";
            automation.RuleOrder = ["note", "switch"];
            automation.Rules = Keyed(
                ("note", Create("Note", AutomationRuleMode.Off, [OnEvent("/Button", "TestPressEvent")], [Set("/Blinds/Position", "7")], inState: "Idle")),
                ("switch", Create("Switch", AutomationRuleMode.On, [OnEvent("/Button", "TestPressEvent")], [Enter("Running")], inState: "Idle")));
        });
        host.Automation.Rules["note"].Mode = AutomationRuleMode.On;
        await host.SaveAsync();

        // Act
        host.Button.Press("Release");
        await host.FlushAsync();

        // Assert
        Assert.Equal("Running", host.Automation.CurrentState);
        Assert.Equal(7, host.Blinds.Position);
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
        await host.ResetAsync();
        var nameAfterReset = host.Weather.Name;
        var triggeredAfterReset = host.Rule("started").LastTriggeredAt;
        await host.RestartAsync();

        // Assert
        Assert.NotNull(triggeredBeforeReset);
        Assert.Null(triggeredAfterReset);
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
        await host.SaveAsync(new Dictionary<string, AutomationRule>());
        var messageBeforeReset = host.Automation.StatusMessage;

        // Act
        await host.ResetAsync();

        // Assert
        Assert.StartsWith("The saved state 'Running' is not a state of the automation", messageBeforeReset);
        Assert.Equal("Idle", host.Automation.CurrentState);
        Assert.Equal(ServiceStatus.Running, host.Automation.Status);
        Assert.Null(host.Automation.StatusMessage);
    }

    [Fact]
    public async Task WhenTheSavedStateWasRemoved_ThenNoRuleRunsAndTheErrorStaysUntilTheReset()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Idle", "Running"];
            automation.InitialState = "Idle";
            automation.Rules = Keyed(
                ("started", Create("Started", AutomationRuleMode.On, [When("/Weather/Power > 500")], [Enter("Running")], inState: "Idle")),
                ("close", On("Close", When("/Weather/Gust > 8"), Invoke("/Blinds/Close"))));
        });
        await SetPowerAsync(host, 600);
        host.Automation.States = ["Idle"];
        host.Automation.RuleOrder = [];
        await host.SaveAsync(Keyed(("close", On("Close", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")))));

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();
        var closesBeforeTheReset = host.Blinds.CloseCount;
        var statusBeforeTheReset = host.Automation.Status;
        var ruleStatusBeforeTheReset = host.Rule("close").Status;
        await host.ResetAsync();

        // Assert
        Assert.Equal(0, closesBeforeTheReset);
        Assert.Equal(ServiceStatus.Error, statusBeforeTheReset);
        Assert.Equal("On: Inactive until Reset state", ruleStatusBeforeTheReset);
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(ServiceStatus.Running, host.Automation.Status);
    }

    [Fact]
    public async Task WhenTheResetCannotBeSaved_ThenTheStateAndTheTriggerTimesStayAndTheErrorIsShown()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(WashingMachine());
        await SetPowerAsync(host, 600);
        host.BreakCheckpointWrites();

        // Act
        await host.ResetAsync();

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
        host.BreakCheckpointWrites();

        // Act
        await SetPowerAsync(host, 600);

        // Assert
        Assert.Equal("Idle", host.Automation.CurrentState);
        Assert.Null(host.Weather.Name);
        Assert.Equal(AutomationRunOutcome.CompletedWithErrors, host.Rule("started").LastRunOutcome);
        Assert.Contains("The state Running was not entered because it could not be saved", host.Rule("started").LastRunMessage);
        Assert.Equal(ServiceStatus.Error, host.Automation.Status);
        Assert.StartsWith("Rule 'Started': action 'enter Running' failed: The state Running was not entered because it could not be saved", host.Automation.StatusMessage);
    }

    [Fact]
    public async Task WhenTheAutomationStops_ThenEveryRuleShowsThatItIsInactive()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("retract", On("Retract", When("/Weather/Gust > 8", TimeSpan.FromSeconds(30)), Invoke("/Blinds/Close"))),
            ("rain", Create("Rain", AutomationRuleMode.DryRun, [When("/Weather/Rain > 0")], [Invoke("/Blinds/Close")])),
            ("off", Create("Off", AutomationRuleMode.Off, [When("/Weather/Rain > 0")], [Invoke("/Blinds/Close")]))));
        host.Weather.Gust = 9;
        await host.FlushAsync();
        var holding = host.Rule("retract").Status;

        // Act
        await host.StopAsync();

        // Assert
        Assert.Equal("On: Holding since 12:00:00 for 00:00:30", holding);
        Assert.Equal("On: Inactive: the automation is stopped", host.Rule("retract").Status);
        Assert.Equal("DryRun: Inactive: the automation is stopped", host.Rule("rain").Status);
        Assert.Equal("Off", host.Rule("off").Status);
    }
}
