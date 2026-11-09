using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor.Testing;
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


    [Fact]
    public async Task WhenTheStateIsReset_ThenARuleThatRunsOnActivationRunsAgainOnceWhileTheConditionStaysTrue()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(InInitialState(When("/Weather/Gust > 5")));
        await SetGustAsync(host, 9);
        await SetGustAsync(host, 10);
        var runsBeforeTheReset = host.Rule("retract").RunCount;

        // Act
        await host.ResetAsync();
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
        await host.ResetAsync();
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
        await host.ResetAsync();
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
        await host.AdvanceAsync(TimeSpan.FromMinutes(6));

        // Act
        await host.ResetAsync();
        await host.AdvanceAsync(TimeSpan.FromMinutes(6));
        var runsBeforeTheNewHoldEnds = host.Rule("retract").RunCount;
        await host.AdvanceAsync(TimeSpan.FromMinutes(4));

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
        host.BreakCheckpointWrites();

        // Act
        await host.ResetAsync();

        // Assert
        Assert.Equal(1L, host.Rule("retract").RunCount);
    }

    [Fact]
    public async Task WhenTheButtonIsPressedAfterTheResetWasRequested_ThenThePressIsHandledAfterTheReset()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("wait", On("Wait", OnEvent("/Button", "TestTiltEvent"), Invoke("/Blinds/Wait"))),
            ("close", On("Close", OnEvent("/Button", "TestPressEvent"), Invoke("/Blinds/Close")))));
        host.Button.Tilt(1);
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsWaiting);

        // Act
        host.Automation.ResetState();
        host.Button.Press("Release");
        host.Blinds.Release();
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(1L, host.Rule("close").RunCount);
    }
}
