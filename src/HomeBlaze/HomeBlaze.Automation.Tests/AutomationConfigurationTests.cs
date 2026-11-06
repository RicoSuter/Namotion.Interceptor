using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Tests.Testing;
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
}
