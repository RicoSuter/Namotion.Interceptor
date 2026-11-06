using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests.Runtime;

public class PropertyTriggerEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WhenAChangeOfAnUnwatchedPropertyArrives_ThenATriggerActivatedLaterReadsTheCurrentValue()
    {
        // Arrange
        var graph = new TestGraph();
        var weather = new TestSensor();
        graph.Add("Weather", weather);
        weather.Gust = 3;
        var engine = new PropertyTriggerEngine(graph.Resolver, new FakeTimeProvider(Now), NullLogger.Instance);
        Assert.True(AutomationCompiler.TryCompileRule("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")), [], out var rule, out var error), error);
        var staleChange = new PropertyChangedInput(new PropertyReference(weather, nameof(TestSensor.Gust)), 9.0, 0, 0);

        // Act
        var changeRequests = engine.OnPropertyChanged([rule], staleChange);
        var applied = engine.Apply([staleChange]);
        rule.IsActivated = true;
        var activationRequests = engine.Activate([rule], [rule]);

        // Assert
        Assert.Empty(changeRequests);
        Assert.Empty(applied);
        Assert.Empty(activationRequests);
        Assert.Equal(TriggerPhase.Armed, rule.PropertyTriggers[0].State.Phase);
    }
}
