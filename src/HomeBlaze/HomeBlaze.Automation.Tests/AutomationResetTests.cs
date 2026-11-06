using HomeBlaze.Automation.Tests.Testing;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationResetTests
{
    private static Action<Automation> InInitialState(PropertyTrigger trigger) => automation =>
    {
        automation.States = ["Idle", "Busy"];
        automation.InitialState = "Idle";
        automation.RuleOrder = ["retract"];
        automation.Rules = Keyed(("retract", Create("Retract blinds", AutomationRuleMode.On, [trigger], [Invoke("/Blinds/Close")], inState: "Idle")));
    };

    private static async Task SetGustAsync(AutomationTestHost host, double gust)
    {
        host.Weather.Gust = gust;
        await host.FlushAsync();
    }

    private static async Task ResetAsync(AutomationTestHost host)
    {
        host.Automation.ResetState();
        await host.FlushAsync();
    }

    [Fact]
    public async Task WhenTheStateIsReset_ThenARuleThatRunsOnActivationRunsAgainOnceWhileTheConditionStaysTrue()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(InInitialState(When("/Weather/Gust > 5")));
        await SetGustAsync(host, 9);
        await SetGustAsync(host, 10);
        var runsBeforeTheReset = host.Rule("retract").RunCount;

        // Act
        await ResetAsync(host);
        var runsAfterTheReset = host.Rule("retract").RunCount;
        await SetGustAsync(host, 11);

        // Assert
        Assert.Equal(1L, runsBeforeTheReset);
        Assert.Equal(2L, runsAfterTheReset);
        Assert.Equal(2L, host.Rule("retract").RunCount);
    }

    [Fact]
    public async Task WhenTheStateIsResetWithAWaitForFalseToTrueTrigger_ThenTheTriggerTakesAFreshBaseline()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(InInitialState(When("/Weather/Gust > 5", activation: TriggerActivation.WaitForFalseToTrue)));
        await SetGustAsync(host, 0);
        await SetGustAsync(host, 9);
        var runsBeforeTheReset = host.Rule("retract").RunCount;

        // Act
        await ResetAsync(host);
        await SetGustAsync(host, 10);
        var runsWhileStillTrue = host.Rule("retract").RunCount;
        await SetGustAsync(host, 2);
        await SetGustAsync(host, 9);

        // Assert
        Assert.Equal(1L, runsBeforeTheReset);
        Assert.Equal(1L, runsWhileStillTrue);
        Assert.Equal(2L, host.Rule("retract").RunCount);
    }

    [Fact]
    public async Task WhenTheStateIsResetWithAWaitForFalseToTrueTriggerAndARearm_ThenTheRearmExpressionAllowsTheNextRun()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(InInitialState(
            When("/Weather/Gust > 8", rearm: "/Weather/Gust < 6", activation: TriggerActivation.WaitForFalseToTrue)));
        await SetGustAsync(host, 0);
        await SetGustAsync(host, 9);

        // Act
        await ResetAsync(host);
        await SetGustAsync(host, 7);
        await SetGustAsync(host, 9);
        var runsWithoutRearm = host.Rule("retract").RunCount;
        await SetGustAsync(host, 5);
        await SetGustAsync(host, 9);

        // Assert
        Assert.Equal(1L, runsWithoutRearm);
        Assert.Equal(2L, host.Rule("retract").RunCount);
    }

    [Fact]
    public async Task WhenTheStateIsResetWhileAHoldRuns_ThenTheHoldRestartsFromTheResetTime()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(InInitialState(When("/Weather/Gust > 5", TimeSpan.FromMinutes(10))));
        await SetGustAsync(host, 9);
        host.Time.Advance(TimeSpan.FromMinutes(6));
        await host.FlushAsync();

        // Act
        await ResetAsync(host);
        host.Time.Advance(TimeSpan.FromMinutes(6));
        await host.FlushAsync();
        var runsBeforeTheNewHoldEnds = host.Rule("retract").RunCount;
        host.Time.Advance(TimeSpan.FromMinutes(4));
        await host.FlushAsync();

        // Assert
        Assert.Equal(0L, runsBeforeTheNewHoldEnds);
        Assert.Equal(1L, host.Rule("retract").RunCount);
    }

    [Fact]
    public async Task WhenTheResetCannotBeSaved_ThenTheRulesAreNotActivatedAgain()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(InInitialState(When("/Weather/Gust > 5")));
        await SetGustAsync(host, 9);
        File.Delete(host.CheckpointPath);
        Directory.CreateDirectory(host.CheckpointPath);

        // Act
        await ResetAsync(host);

        // Assert
        Assert.Equal(1L, host.Rule("retract").RunCount);
    }
}
