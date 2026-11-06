using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor.Testing;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationResynchronizationTests
{
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

        // Act
        host.Weather.Power = 0;
        await host.FlushAsync();
        host.Weather.Power = 1;
        await host.FlushAsync();

        // Assert
        Assert.Equal("Right", stateWhenStopped);
        Assert.StartsWith($"A feedback chain of more than {AutomationInputQueue.MaxChainDepth} inputs was stopped", statusWhenStopped);
        Assert.True(host.Rule("left").RunCount > runsWhenStopped, $"The rule 'left' ran {host.Rule("left").RunCount} times, {runsWhenStopped} before.");
    }
}
