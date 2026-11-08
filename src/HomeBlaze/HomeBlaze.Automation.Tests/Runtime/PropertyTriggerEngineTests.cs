using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Microsoft.Extensions.Logging;
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
        var engine = new PropertyTriggerEngine(graph.Resolver, new FakeTimeProvider(Now), NullLogger.Instance, () => "Storm", _ => { });
        var rule = Compile("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")));
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

    [Fact]
    public void WhenATriggerBecomesUnknownAndKnownAgain_ThenTheLogsNameTheAutomation()
    {
        // Arrange
        var graph = new TestGraph();
        var weather = new TestSensor();
        graph.Add("Weather", weather);
        var logger = new MessageLogger();
        var engine = new PropertyTriggerEngine(graph.Resolver, new FakeTimeProvider(Now), logger, () => "Storm", _ => { });
        var rule = Compile("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")));
        rule.IsActivated = true;

        // Act
        engine.Activate([rule], [rule]);
        engine.OnPropertyChanged([rule], new PropertyChangedInput(new PropertyReference(weather, nameof(TestSensor.Gust)), 3.0, 0, 0));

        // Assert
        Assert.Equal(
        [
            "The trigger 1 (/Weather/Gust > 8) of rule Retract in automation Storm is unknown, missing /Weather/Gust.",
            "The trigger 1 (/Weather/Gust > 8) of rule Retract in automation Storm is known again."
        ], logger.Messages.ToArray());
    }
}
