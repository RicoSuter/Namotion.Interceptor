using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests.Runtime;

public class AutomationTriggersTests
{
    [Fact]
    public void WhenAGraphChangeLeavesTheStructureAsItWas_ThenThePathsAreNotResolvedAgain()
    {
        // Arrange
        var graph = new TestGraph();
        graph.Add("Weather", new TestSensor());
        var registry = graph.Context.GetService<ISubjectRegistry>();
        var publishes = 0;
        var time = new FakeTimeProvider(AutomationTestHost.Start);
        var queue = new AutomationInputQueue();
        var routes = new GraphRoutes(automation: null, changes: null);
        var triggers = new AutomationTriggers(
            queue,
            () => registry.StructureVersion,
            new PropertyTriggerEngine(graph.Resolver, time, NullLogger.Instance, () => "Storm", _ => publishes++, routes),
            new EventTriggerEngine(graph.Resolver, time, queue, routes),
            routes);
        var rule = Compile("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")));
        rule.IsActivated = true;
        triggers.Activate([rule], [rule]);
        triggers.OnGraphChanged([rule]);
        var publishesAfterTheFirstGraphChange = publishes;

        // Act
        graph.Root.Children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children);
        triggers.OnGraphChanged([rule]);
        var publishesAfterTheSameChildren = publishes;
        graph.Add("Blinds", new TestBlinds());
        triggers.OnGraphChanged([rule]);

        // Assert
        Assert.Equal(publishesAfterTheFirstGraphChange, publishesAfterTheSameChildren);
        Assert.Equal(publishesAfterTheFirstGraphChange + 1, publishes);
    }

    // The paths that resolve at the same version, or while a change is in progress, may be out of date, as they are
    // without routes, until a graph change at another version resolves them again.
    [Theory]
    [InlineData(2L, 2L)]
    [InlineData(2L, 3L)]
    public void WhenAGraphChangeFindsAVersionWhoseResolutionsMayBeOutOfDate_ThenTheRoutesStayUnboundedUntilAnotherVersion(long first, long second)
    {
        // Arrange
        var graph = new TestGraph();
        graph.Add("Weather", new TestSensor());
        var version = first;
        var time = new FakeTimeProvider(AutomationTestHost.Start);
        var queue = new AutomationInputQueue();
        var routes = new GraphRoutes(automation: null, changes: null);
        var triggers = new AutomationTriggers(
            queue,
            () => version,
            new PropertyTriggerEngine(graph.Resolver, time, NullLogger.Instance, () => "Storm", _ => { }, routes),
            new EventTriggerEngine(graph.Resolver, time, queue, routes),
            routes);
        var rule = Compile("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")));
        rule.IsActivated = true;
        triggers.Activate([rule], [rule]);
        triggers.OnGraphChanged([rule]);
        var isUnboundedAtTheFirstVersion = routes.IsUnbounded;

        // Act
        version = second;
        triggers.OnGraphChanged([rule]);
        var isUnboundedAtTheSecondVersion = routes.IsUnbounded;
        version = 4;
        triggers.OnGraphChanged([rule]);

        // Assert
        Assert.False(isUnboundedAtTheFirstVersion);
        Assert.True(isUnboundedAtTheSecondVersion);
        Assert.False(routes.IsUnbounded);
    }

    [Fact]
    public void WhenEqualTriggersAreCarriedOver_ThenTheyPairUpInOrderWhereverTheyMoved()
    {
        // Arrange
        static CompiledRule Compile(params AutomationTrigger[] triggers) =>
            RuleBuilder.Compile("retract", Create("Retract", AutomationRuleMode.On, triggers, [Invoke("/Blinds/Close")]));

        var old = Compile(When("/Weather/Rain > 0"), When("/Weather/Gust > 8"), When("/Weather/Rain > 0"));
        var first = old.PropertyTriggers[0].State;
        var third = old.PropertyTriggers[2].State;
        var rule = Compile(When("/Weather/Rain > 0"), When("/Weather/Rain > 0"));

        // Act
        AutomationTriggers.CarryOver(old, rule);

        // Assert
        Assert.Same(first, rule.PropertyTriggers[0].State);
        Assert.Same(third, rule.PropertyTriggers[1].State);
    }
}
