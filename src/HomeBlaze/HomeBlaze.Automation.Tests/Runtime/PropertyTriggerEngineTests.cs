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

    [Fact]
    public void WhenAGraphChangeChangesNoPropertyOfARule_ThenItsPendingTriggersKeepTheirOrderAndTheNextDueTimeStays()
    {
        // Arrange
        var graph = new TestGraph();
        var weather = new TestSensor { LastUpdated = Now };
        graph.Add("Weather", weather);
        var time = new FakeTimeProvider(Now);
        var engine = new PropertyTriggerEngine(graph.Resolver, time, NullLogger.Instance, () => "Storm", _ => { });
        var rule = Compile("storm", Create(
            "Storm", AutomationRuleMode.On, [When("/Weather/Gust > 8"), When("/Weather/Rain > 0")], [Invoke("/Blinds/Close")],
            "now - /Weather/LastUpdated > 20s"));
        rule.IsActivated = true;
        engine.Activate([rule], [rule]);
        weather.Rain = 1;
        engine.TrackPending([rule], engine.OnPropertyChanged([rule], new PropertyChangedInput(new PropertyReference(weather, nameof(TestSensor.Rain)), 1.0, 0, 0)));
        weather.Gust = 9;
        engine.TrackPending([rule], engine.OnPropertyChanged([rule], new PropertyChangedInput(new PropertyReference(weather, nameof(TestSensor.Gust)), 9.0, 0, 0)));
        time.Advance(TimeSpan.FromSeconds(30));
        var dueBefore = engine.GetNextDueAt([rule], time.GetUtcNow());

        // Act
        graph.Add("Holder", new TestSensorHolder());
        var requests = engine.OnGraphChanged([rule], evaluateAll: false);

        // Assert
        Assert.Empty(requests);
        Assert.Equal(Now.AddSeconds(20).AddTicks(1), dueBefore);
        Assert.Equal(dueBefore, engine.GetNextDueAt([rule], time.GetUtcNow()));
        Assert.Equal(
            [rule.PropertyTriggers[1], rule.PropertyTriggers[0]],
            engine.RequestPendingRuns([rule]).Select(request => request.Trigger).ToArray());
    }
}
