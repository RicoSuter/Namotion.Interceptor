using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor.Testing;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationConfigurationTests
{
    private static AutomationRule Retract(string expression = "/Weather/Gust > 8") => On("Retract", When(expression), Invoke("/Blinds/Close"));

    private static async Task SaveAsync(AutomationTestHost host, Dictionary<string, AutomationRule> rules)
    {
        host.Automation.Rules = rules;
        await host.Automation.ApplyConfigurationAsync(CancellationToken.None);
        await host.FlushAsync();
    }

    [Fact]
    public async Task WhenTheConfigurationIsSavedWithAnUnchangedTrigger_ThenTheRuleDoesNotRunAgain()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(("retract", Retract())));
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Act
        await SaveAsync(host, Keyed(("retract", Retract())));

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(1L, host.Rule("retract").RunCount);
        Assert.Equal("On: Triggered, waiting to rearm", host.Rule("retract").Status);
    }

    [Fact]
    public async Task WhenATriggerIsChanged_ThenItIsActivatedAgain()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(("retract", Retract())));
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Act
        await SaveAsync(host, Keyed(("retract", Retract("/Weather/Gust > 5"))));

        // Assert
        Assert.Equal(2, host.Blinds.CloseCount);
        Assert.Equal(2L, host.Rule("retract").RunCount);
    }

    [Fact]
    public async Task WhenTheConfigurationIsInvalid_ThenThePreviousConfigurationKeepsRunning()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(("retract", Retract())));

        // Act
        await SaveAsync(host, Keyed(("retract", Retract("/Weather/Gust >"))));
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, host.Automation.Status);
        Assert.StartsWith(
            "The configuration is invalid and was not applied; the previous one keeps running: Rule 'Retract': trigger 1: ",
            host.Automation.StatusMessage);
        Assert.Equal("On: Triggered, waiting to rearm", host.Rule("retract").Status);
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(1L, host.Rule("retract").RunCount);
    }

    [Fact]
    public async Task WhenARuleIsRemoved_ThenItsRunsLeaveTheCheckpoint()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("retract", Retract()),
            ("rain", On("Rain", When("/Weather/Rain > 0"), Invoke("/Blinds/Close")))));
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Act
        await SaveAsync(host, Keyed(("rain", On("Rain", When("/Weather/Rain > 0"), Invoke("/Blinds/Close")))));

        // Assert
        Assert.DoesNotContain("\"retract\"", await File.ReadAllTextAsync(host.CheckpointPath));
    }

    [Fact]
    public async Task WhenTheConfigurationIsSavedWhileEventsAreQueued_ThenTheEventsOfAnUnchangedTriggerStillRun()
    {
        // Arrange
        static AutomationRule Press() => Create(
            "Press", AutomationRuleMode.On, [OnEvent("/Button", "TestPressEvent", "event/Gesture == Release")], [Invoke("/Blinds/Close")]);
        static AutomationRule Wait() => On("Wait", When("/Weather/Rain > 0"), Invoke("/Blinds/Wait"));
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(("press", Press()), ("wait", Wait())));
        host.Weather.Rain = 1;
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsWaiting);
        host.Button.Press("Release");
        host.Button.Press("Release");

        // Act
        host.Automation.Name = "Renamed";
        host.Automation.Rules = Keyed(("press", Press()), ("wait", Wait()));
        await host.Automation.ApplyConfigurationAsync(CancellationToken.None);
        host.Blinds.Release();
        await host.FlushAsync();
        host.Button.Press("Release");
        await host.FlushAsync();

        // Assert
        Assert.Equal(3, host.Blinds.CloseCount);
        Assert.Equal(3L, host.Rule("press").RunCount);
    }

    [Fact]
    public async Task WhenTheFilterOfAnEventTriggerIsChanged_ThenTheTriggerUsesTheNewFilter()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("press", Create("Press", AutomationRuleMode.On, [OnEvent("/Button", "TestPressEvent", "event/Gesture == Release")], [Invoke("/Blinds/Close")]))));

        // Act
        await SaveAsync(host, Keyed(
            ("press", Create("Press", AutomationRuleMode.On, [OnEvent("/Button", "TestPressEvent", "event/Gesture == Hold")], [Invoke("/Blinds/Close")]))));
        host.Button.Press("Release");
        host.Button.Press("Hold");
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenARunEndsAfterTheRulesWereReplaced_ThenTheNewRuleKeepsTheRun()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("wait", On("Wait", When("/Weather/Rain > 0"), Invoke("/Blinds/Wait")))));
        host.Weather.Rain = 1;
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsWaiting);
        var replacement = On("Wait", When("/Weather/Rain > 0"), Invoke("/Blinds/Wait"));

        // Act
        host.Automation.Rules = Keyed(("wait", replacement));
        await host.Automation.ApplyConfigurationAsync(CancellationToken.None);
        host.Blinds.Release();
        await host.FlushAsync();

        // Assert
        Assert.Same(replacement, host.Rule("wait"));
        Assert.Equal(1L, replacement.RunCount);
        Assert.Equal(AutomationRunOutcome.Succeeded, replacement.LastRunOutcome);
        Assert.Equal(AutomationTestHost.Start, replacement.LastTriggeredAt);
    }

    [Fact]
    public async Task WhenValidRulesAreSetButNotApplied_ThenTheNewRuleSaysItIsNotAppliedYet()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(("retract", Retract())));

        // Act
        host.Automation.Rules = Keyed(("retract", Retract("/Weather/Gust > 5")));
        await host.FlushAsync();

        // Assert
        Assert.Equal("Not applied yet", host.Rule("retract").Status);
    }

    [Fact]
    public async Task WhenAnInvalidConfigurationAddsARule_ThenTheNewRuleSaysTheConfigurationIsInvalid()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(("retract", Retract())));

        // Act
        await SaveAsync(host, Keyed(("retract", Retract()), ("broken", Retract("/Weather/Gust >"))));

        // Assert
        Assert.Equal("Not applied: the configuration is invalid", host.Rule("broken").Status);
    }

    [Fact]
    public async Task WhenAnActionHasNoArgumentsAtTheStart_ThenTheConfigurationIsShownAsInvalid()
    {
        // Arrange
        var rule = On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close") with { Arguments = null! });

        // Act
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(("retract", rule)));
        await host.FlushAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, host.Automation.Status);
        Assert.Equal(
            "The configuration is invalid and was not applied; the previous one keeps running: Rule 'Retract': action 'Close': the arguments are missing",
            host.Automation.StatusMessage);
        Assert.Equal("Not applied: the configuration is invalid", host.Rule("retract").Status);
    }

    [Fact]
    public async Task WhenASaveHasAnActionWithoutArguments_ThenThePreviousConfigurationKeepsRunning()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(("retract", Retract())));
        var broken = On("Retract", When("/Weather/Gust > 5"), Invoke("/Blinds/Close") with { Arguments = null! });

        // Act
        await SaveAsync(host, Keyed(("retract", broken)));
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal(
            "The configuration is invalid and was not applied; the previous one keeps running: Rule 'Retract': action 'Close': the arguments are missing",
            host.Automation.StatusMessage);
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(1L, host.Rule("retract").RunCount);
    }

    [Fact]
    public async Task WhenARuleIsNull_ThenTheConfigurationIsShownAsInvalid()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(("retract", Retract())));

        // Act
        await SaveAsync(host, new Dictionary<string, AutomationRule> { ["retract"] = Retract(), ["broken"] = null! });

        // Assert
        Assert.Equal(
            "The configuration is invalid and was not applied; the previous one keeps running: Rule 'broken': it has no trigger; it has no action",
            host.Automation.StatusMessage);
        Assert.Equal("Not applied: the configuration is invalid", host.Rule("broken").Status);
    }
}
