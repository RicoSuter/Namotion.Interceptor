using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Runtime;
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
    public async Task WhenAnEventContinuesAChainBeyondTheLimitAndARuleHandlesIt_ThenTheChainIsStoppedAndReported()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("close", On("Close", OnEvent("/Button", "TestPressEvent"), Invoke("/Blinds/Close")))));

        // Act
        PressInChainBeyondTheLimit(host);
        await host.FlushAsync();

        // Assert
        Assert.Equal(0, host.Blinds.CloseCount);
        Assert.Equal(ServiceStatus.Error, host.Automation.Status);
        Assert.StartsWith("A feedback chain of more than 16 inputs was stopped", host.Automation.StatusMessage);
    }

    [Fact]
    public async Task WhenAnEventContinuesAChainBeyondTheLimitAndOnlyARuleOfAnotherStateListens_ThenNoChainIsReported()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Idle", "Running"];
            automation.InitialState = "Idle";
            automation.Rules = Keyed(
                ("close", Create("Close", AutomationRuleMode.On, [OnEvent("/Button", "TestPressEvent")], [Invoke("/Blinds/Close")], inState: "Running")));
        });

        // Act
        PressInChainBeyondTheLimit(host);
        await host.FlushAsync();

        // Assert
        Assert.Equal(0, host.Blinds.CloseCount);
        Assert.Equal(ServiceStatus.Running, host.Automation.Status);
        Assert.Null(host.Automation.StatusMessage);
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

    private static void PressInChainBeyondTheLimit(AutomationTestHost host)
    {
        using var run = AutomationInputQueue.EnterRun(AutomationInputQueue.MaxChainDepth);
        host.Button.Press("Release");
    }
}
