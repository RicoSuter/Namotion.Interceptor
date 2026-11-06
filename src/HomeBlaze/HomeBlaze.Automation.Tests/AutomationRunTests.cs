using System.Reactive;
using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Tests.Testing;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationRunTests
{
    [Fact]
    public async Task WhenARuleIsADryRun_ThenTheRunIsRecordedAndNothingIsExecuted()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Day", "Night"];
            automation.InitialState = "Day";
            automation.Rules = Keyed(("retract", Create(
                "Retract",
                AutomationRuleMode.DryRun,
                [When("/Weather/Gust > 8")],
                [Invoke("/Blinds/MoveTo", ("position", "/Weather/Gust * 2")), Enter("Night")])));
        });
        var rule = host.Rule("retract");

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal(1L, rule.RunCount);
        Assert.Equal(AutomationRunOutcome.DryRun, rule.LastRunOutcome);
        Assert.Equal("Would run /Blinds/MoveTo(position: 18); ChangeState(Night)", rule.LastRunMessage);
        Assert.Equal(0, host.Blinds.Position);
        Assert.Equal("Day", host.Automation.CurrentState);
        Assert.Null(rule.LastTriggeredAt);
        Assert.Equal("DryRun: Triggered, waiting to rearm", rule.Status);
    }

    [Fact]
    public async Task WhenAnActionFails_ThenTheStatusShowsTheErrorUntilAFullySuccessfulRun()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("jam", On("Jam", When("/Weather/Gust > 8"), Invoke("/Blinds/Jam"))),
            ("close", On("Close", When("/Weather/Rain > 0"), Invoke("/Blinds/Close")))));

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();
        var statusAfterFailure = host.Automation.Status;
        var messageAfterFailure = host.Automation.StatusMessage;
        host.Weather.Rain = 1;
        await host.FlushAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, statusAfterFailure);
        Assert.Equal("Rule 'Jam': action 'Jam' failed: /Blinds/Jam() failed: The motor is jammed.", messageAfterFailure);
        Assert.Equal(AutomationRunOutcome.CompletedWithErrors, host.Rule("jam").LastRunOutcome);
        Assert.Equal(ServiceStatus.Running, host.Automation.Status);
        Assert.Null(host.Automation.StatusMessage);
    }

    [Fact]
    public async Task WhenAStateChangeSubscriberFails_ThenTheStateIsEnteredAndTheRunSucceeds()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Day", "Night"];
            automation.InitialState = "Day";
            automation.Rules = Keyed(("night", On("Night", When("/Weather/Gust > 8"), Enter("Night"))));
        });
        using var subscription = host.Automation.Subscribe(
            Observer.Create<AutomationStateChangedEvent>(_ => throw new InvalidOperationException("The subscriber failed.")));

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal("Night", host.Automation.CurrentState);
        Assert.Equal(AutomationRunOutcome.Succeeded, host.Rule("night").LastRunOutcome);
        Assert.Equal("Ran ChangeState(Night)", host.Rule("night").LastRunMessage);
    }

    [Fact]
    public async Task WhenARuleIsOff_ThenItIgnoresItsTriggers()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("retract", Create("Retract", AutomationRuleMode.Off, [When("/Weather/Gust > 8")], [Invoke("/Blinds/Close")]))));

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal(0, host.Blinds.CloseCount);
        Assert.Equal(0L, host.Rule("retract").RunCount);
        Assert.Equal("Off", host.Rule("retract").Status);
    }

    [Fact]
    public async Task WhenTheAutomationIsDisabled_ThenNoRuleRuns()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.Enabled = false;
            automation.Rules = Keyed(("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close"))));
        });

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal(ServiceStatus.Stopped, host.Automation.Status);
        Assert.Equal("On: Inactive: the automation is disabled", host.Rule("retract").Status);
        Assert.Equal(0, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenRunsAreRecorded_ThenTheCheckpointKeepsThemAtAConstantSize()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
            automation.Rules = Keyed(("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")))));
        host.Weather.Gust = 9;
        await host.FlushAsync();
        var sizeAfterOneRun = new FileInfo(host.CheckpointPath).Length;

        // Act
        host.Weather.Gust = 5;
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal(sizeAfterOneRun, new FileInfo(host.CheckpointPath).Length);
        Assert.Contains("\"runCount\": 2", await File.ReadAllTextAsync(host.CheckpointPath));
    }
}
