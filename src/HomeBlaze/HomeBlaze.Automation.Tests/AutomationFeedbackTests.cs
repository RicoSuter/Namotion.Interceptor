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

    // Each run replaces the sensor, a structural change on the route of the rules, so the chain continues through graph
    // changes; the flushes are bounded so that a chain that does not stop fails the test instead of hanging it.
    [Theory]
    [InlineData("no Only if")]
    [InlineData("Only if on a fixed path")]
    [InlineData("Only if on the replaced subject")]
    [InlineData("rearm expression")]
    [InlineData("event trigger in the chain")]
    [InlineData("rule in a state the chain enters")]
    public async Task WhenRulesReplaceTheSubjectTheirTriggersReadEndlessly_ThenTheChainIsStoppedAndDoesNotRestart(string shape)
    {
        // Arrange
        var replacer = new TestReplacer { Sensor = new TestSensor { Gust = 1 } };
        await using var host = await AutomationTestHost.StartAsync(automation => ConfigureReplacingRules(automation, shape));

        // Act
        host.Graph.Add("Replacer", replacer);
        await host.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var replacementsAfterTheStop = replacer.Replacements;
        await host.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(ServiceStatus.Error, host.Automation.Status);
        Assert.StartsWith("A feedback chain of more than 16 inputs was stopped", host.Automation.StatusMessage);
        Assert.InRange(replacementsAfterTheStop, 1, AutomationInputQueue.MaxChainDepth + 2);
        Assert.Equal(replacementsAfterTheStop, replacer.Replacements);
    }

    // The dropped graph change belongs to the overload, so the triggers it leads elsewhere run in the make-up for the
    // overload, and no feedback chain is reported.
    [Fact]
    public async Task WhenTheQueueOverflowsWithAGraphChange_ThenTheTriggersItLeadsElsewhereRunOnceAndNoChainIsReported()
    {
        // Arrange
        var replacer = new TestReplacer { Sensor = new TestSensor { Gust = 1 } };
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("wait", On("Wait", When("/Weather/Rain > 0"), Invoke("/Blinds/Wait"))),
            ("gust", Create("Gust", AutomationRuleMode.DryRun, [When("/Weather/Gust > 1000")], [Invoke("/Blinds/Open")])),
            ("high", On("High", When("/Replacer/Sensor/Gust > 8"), Invoke("/Blinds/Close")))));
        host.Graph.Add("Replacer", replacer);
        await host.FlushAsync();
        host.Weather.Rain = 1;
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsWaiting);

        // Act
        for (var gust = 1; gust <= 1100; gust++)
        {
            host.Weather.Gust = gust;
        }

        replacer.Replace();
        host.Blinds.Release();
        await host.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(1L, host.Rule("high").RunCount);
        Assert.DoesNotContain(host.Logger.Messages, message => message.Contains("feedback chain"));
    }

    // Both rules replace the sensor, one when its gust is high and one when it is low, in the shape given.
    private static void ConfigureReplacingRules(Automation automation, string shape)
    {
        var replace = Invoke("/Replacer/Replace");
        automation.Rules = shape switch
        {
            "no Only if" => Keyed(
                ("high", On("High", When("/Replacer/Sensor/Gust > 8"), replace)),
                ("low", On("Low", When("/Replacer/Sensor/Gust < 5"), replace))),
            "Only if on a fixed path" => Keyed(
                ("high", Create("High", AutomationRuleMode.On, [When("/Replacer/Sensor/Gust > 8")], [replace], onlyIf: "/Weather/Gust == null")),
                ("low", Create("Low", AutomationRuleMode.On, [When("/Replacer/Sensor/Gust < 5")], [replace], onlyIf: "/Weather/Gust == null"))),
            "Only if on the replaced subject" => Keyed(
                ("high", Create("High", AutomationRuleMode.On, [When("/Replacer/Sensor/Gust > 8")], [replace], onlyIf: "/Replacer/Sensor/Gust > 0")),
                ("low", Create("Low", AutomationRuleMode.On, [When("/Replacer/Sensor/Gust < 5")], [replace], onlyIf: "/Replacer/Sensor/Gust > 0"))),
            "rearm expression" => Keyed(
                ("high", On("High", When("/Replacer/Sensor/Gust > 8", rearm: "/Replacer/Sensor/Gust < 5"), replace)),
                ("low", On("Low", When("/Replacer/Sensor/Gust < 5", rearm: "/Replacer/Sensor/Gust > 8"), replace))),
            "event trigger in the chain" => Keyed(
                ("high", On("High", When("/Replacer/Sensor/Gust > 8"), Invoke("/Button/Press", ("gesture", "single")))),
                ("pressed", Create("Pressed", AutomationRuleMode.On, [OnEvent("/Button", "TestPressEvent")], [replace])),
                ("low", On("Low", When("/Replacer/Sensor/Gust < 5"), replace))),
            "rule in a state the chain enters" => CreateStateRules(automation, replace),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "The shape has no rules.")
        };
    }

    private static Dictionary<string, AutomationRule> CreateStateRules(Automation automation, AutomationAction replace)
    {
        automation.States = ["Day", "Night"];
        automation.InitialState = "Day";
        return Keyed(
            ("low", Create("Low", AutomationRuleMode.On, [When("/Replacer/Sensor/Gust < 5")], [replace, Enter("Night")], inState: "Day")),
            ("high", Create("High", AutomationRuleMode.On, [When("/Replacer/Sensor/Gust > 8")], [replace, Enter("Day")], inState: "Night")));
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
