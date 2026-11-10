using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor.Testing;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationResynchronizationTests
{
    // Two rules that enter each other's state run this often each for one input from outside, until the chain of the
    // inputs their runs cause reaches its limit.
    private const int RunsOfEachRuleInAChain = (AutomationInputQueue.MaxChainDepth / 2) + 1;

    [Fact]
    public async Task WhenAPropertyChangeIsDroppedByAFullQueue_ThenTheRulesAreEvaluatedWithTheCurrentValue()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("wait", On("Wait", When("/Weather/Rain > 0"), Invoke("/Blinds/Wait"))),
            ("gust", Create("Gust", AutomationRuleMode.DryRun, [When("/Weather/Gust > 5000")], [Invoke("/Blinds/Close")]))));
        host.Weather.Rain = 1;
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsWaiting);
        for (var gust = 1; gust <= AutomationInputQueue.Capacity; gust++)
        {
            host.Weather.Gust = gust;
        }

        // Act
        host.Weather.Gust = 9000;
        host.Blinds.Release();
        await host.FlushAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, host.Automation.Status);
        Assert.StartsWith("The input queue overflowed", host.Automation.StatusMessage);
        Assert.Equal(1L, host.Rule("gust").RunCount);
    }

    [Fact]
    public async Task WhenAPulseWasQueuedBeforeTheQueueOverflowed_ThenItStillRunsTheRule()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("wait", On("Wait", When("/Weather/Rain > 0"), Invoke("/Blinds/Wait"))),
            ("gust", Create("Gust", AutomationRuleMode.DryRun, [When("/Weather/Gust > 5000")], [Invoke("/Blinds/Close")]))));
        host.Weather.Rain = 1;
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsWaiting);
        host.Weather.Gust = 9000;
        host.Weather.Gust = 0;
        for (var gust = 1; gust <= AutomationInputQueue.Capacity - 2; gust++)
        {
            host.Weather.Gust = gust;
        }

        // Act
        host.Weather.Gust = 1;
        host.Blinds.Release();
        await host.FlushAsync();

        // Assert
        Assert.StartsWith("The input queue overflowed", host.Automation.StatusMessage);
        Assert.Equal(1L, host.Rule("gust").RunCount);
    }

    [Fact]
    public async Task WhenAnExternalChangeIsQueuedAsAFeedbackChainIsStopped_ThenItsRuleStillRuns()
    {
        // Arrange
        var gate = new TestGate { BlockOnCall = RunsOfEachRuleInAChain };
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Left", "Right"];
            automation.InitialState = "Left";
            automation.Rules = Keyed(
                ("right", Create("Right", AutomationRuleMode.On, [When("/Weather/Power > 0")], [Invoke("/Gate/Step"), Enter("Right")], inState: "Left")),
                ("left", Create("Left", AutomationRuleMode.On, [When("/Weather/Power > 0")], [Enter("Left")], inState: "Right")),
                ("door", On("Door", When("/Weather/Gust > 8"), Invoke("/Blinds/Close"))));
        });
        host.Graph.Add("Gate", gate);
        await host.FlushAsync();
        host.Weather.Power = 1;
        await AsyncTestHelpers.WaitUntilAsync(() => gate.IsWaiting);

        // Act
        host.Weather.Gust = 9;
        gate.Release();
        await host.FlushAsync();

        // Assert
        Assert.StartsWith("A feedback chain", host.Automation.StatusMessage);
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(1L, host.Rule("door").RunCount);
    }

    [Fact]
    public async Task WhenAFeedbackChainOfStateChangesIsStopped_ThenTheRulesOfTheLastStateStillObserveTheirTriggers()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Left", "Right"];
            automation.InitialState = "Left";
            automation.Rules = Keyed(
                ("right", Create("Right", AutomationRuleMode.On, [When("/Weather/Power > 0")], [Enter("Right")], inState: "Left")),
                ("left", Create("Left", AutomationRuleMode.On, [When("/Weather/Power > 0")], [Enter("Left")], inState: "Right")));
        });
        host.Weather.Power = 1;
        await host.FlushAsync();
        var stateWhenStopped = host.Automation.CurrentState;
        var statusWhenStopped = host.Automation.StatusMessage;
        var runsWhenStopped = host.Rule("left").RunCount;
        var outcomeWhenStopped = host.Rule("left").LastRunOutcome;
        var ruleStatusWhenStopped = host.Rule("left").Status;

        // Act
        host.Weather.Power = 0;
        await host.FlushAsync();
        host.Weather.Power = 1;
        await host.FlushAsync();

        // Assert
        Assert.Equal("Right", stateWhenStopped);
        Assert.StartsWith($"A feedback chain of more than {AutomationInputQueue.MaxChainDepth} inputs was stopped", statusWhenStopped);
        Assert.Equal(AutomationRunOutcome.Succeeded, outcomeWhenStopped);
        Assert.Contains($"a feedback chain of more than {AutomationInputQueue.MaxChainDepth} inputs was stopped", ruleStatusWhenStopped);
        Assert.Equal(RunsOfEachRuleInAChain, host.Rule("left").RunCount - runsWhenStopped);
    }
}
