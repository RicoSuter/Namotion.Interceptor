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
        var time = new FakeTimeProvider(AutomationTestHost.Start);
        var queue = new AutomationInputQueue();
        var routes = new GraphRoutes(automation: new TestSensor(), changes: new AutomationChangeReceiver(new AutomationInputQueue()));
        var triggers = new AutomationTriggers(
            queue,
            () => registry.StructureVersion,
            new PropertyTriggerEngine(graph.Resolver, time, NullLogger.Instance, () => "Storm", _ => { }, routes),
            new EventTriggerEngine(graph.Resolver, queue, NullLogger.Instance, () => "Storm", routes));
        var rule = Compile("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")));
        rule.IsActivated = true;
        triggers.Activate([rule], [rule]);
        triggers.GetRequests(new ActivationInput([rule], 0), [rule]);
        triggers.OnGraphChanged([rule]);
        var trigger = rule.PropertyTriggers[0];
        var dependenciesAfterTheFirstGraphChange = trigger.Dependencies;

        // Act
        graph.Root.Children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children);
        triggers.OnGraphChanged([rule]);
        var dependenciesAfterTheSameChildren = trigger.Dependencies;
        graph.Add("Blinds", new TestBlinds());
        triggers.OnGraphChanged([rule]);

        // Assert
        // Each resolution stores a new array, also for equal dependencies.
        Assert.Same(dependenciesAfterTheFirstGraphChange, dependenciesAfterTheSameChildren);
        Assert.NotSame(dependenciesAfterTheFirstGraphChange, trigger.Dependencies);
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
        AutomationTriggers.CarryOverStates(old, rule);

        // Assert
        Assert.Same(first, rule.PropertyTriggers[0].State);
        Assert.Same(third, rule.PropertyTriggers[1].State);
    }
}
