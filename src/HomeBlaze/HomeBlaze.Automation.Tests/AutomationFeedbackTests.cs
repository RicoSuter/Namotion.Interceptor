using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor.Testing;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationFeedbackTests
{
    [Fact]
    public async Task WhenRulesTriggerEachOtherEndlessly_ThenTheChainIsStoppedAndReported()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("ping", On("Ping", When("/Weather/Name == ping"), Set("/Weather/Name", "pong"))),
            ("pong", On("Pong", When("/Weather/Name == pong"), Set("/Weather/Name", "ping")))));

        // Act
        host.Weather.Name = "ping";
        await host.FlushAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, host.Automation.Status);
        Assert.StartsWith("A feedback chain of more than 16 inputs was stopped", host.Automation.StatusMessage);
    }

    // Each run replaces the sensor, a structural change on the route of both rules, so the chain continues through graph
    // changes; the flush is bounded so that a chain that does not stop fails the test instead of hanging it.
    [Fact]
    public async Task WhenRulesReplaceTheSubjectTheirTriggersReadEndlessly_ThenTheChainIsStoppedAndDoesNotRestart()
    {
        // Arrange
        var replacer = new TestReplacer { Sensor = new TestSensor { Gust = 1 } };
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("high", On("High", When("/Replacer/Sensor/Gust > 8"), Invoke("/Replacer/Replace"))),
            ("low", On("Low", When("/Replacer/Sensor/Gust < 5"), Invoke("/Replacer/Replace")))));

        // Act
        host.Graph.Add("Replacer", replacer);
        await host.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var replacementsAfterTheStop = replacer.Replacements;
        await host.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(ServiceStatus.Error, host.Automation.Status);
        Assert.StartsWith("A feedback chain of more than 16 inputs was stopped", host.Automation.StatusMessage);
        Assert.True(replacementsAfterTheStop > 0);
        Assert.Equal(replacementsAfterTheStop, replacer.Replacements);
    }

    [Fact]
    public async Task WhenRulesResetTheStateAndEnterItAgainEndlessly_ThenTheChainIsStoppedAndReported()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Idle", "Running"];
            automation.InitialState = "Idle";
            automation.Rules = Keyed(
                ("start", Create("Start", AutomationRuleMode.On, [When("/Weather/Power > 500")], [Enter("Running")], inState: "Idle")),
                ("reset", Create("Reset", AutomationRuleMode.On, [OnEvent("..", "AutomationStateChangedEvent", "event/NewState == Running")], [Invoke("../ResetState")])),
                ("again", Create("Again", AutomationRuleMode.On, [OnEvent("..", "AutomationStateChangedEvent", "event/NewState == Idle")], [Enter("Running")])));
        });

        // Act
        host.Weather.Power = 600;
        await host.FlushAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, host.Automation.Status);
        Assert.Contains("A feedback chain of more than 16 inputs was stopped", host.Automation.StatusMessage);
    }

    [Fact]
    public async Task WhenTheQueueOverflows_ThenTheDropIsReportedAndTheRemainingInputsAreProcessed()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("wait", On("Wait", When("/Weather/Rain > 0"), Invoke("/Blinds/Wait"))),
            ("gust", Create("Gust", AutomationRuleMode.DryRun, [When("/Weather/Gust > 1000")], [Invoke("/Blinds/Close")]))));
        host.Weather.Rain = 1;
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsWaiting);

        // Act
        for (var gust = 1; gust <= 1100; gust++)
        {
            host.Weather.Gust = gust;
        }

        host.Blinds.Release();
        await AsyncTestHelpers.WaitUntilAsync(() => host.Rule("gust").RunCount == 1);
        await host.FlushAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, host.Automation.Status);
        Assert.StartsWith("The input queue overflowed", host.Automation.StatusMessage);
        Assert.Equal(1L, host.Rule("gust").RunCount);
    }
}
