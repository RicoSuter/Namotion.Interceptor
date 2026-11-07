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
        var triggers = new AutomationTriggers(
            graph.Resolver, new FakeTimeProvider(AutomationTestHost.Start), new AutomationInputQueue(), NullLogger.Instance, () => "Storm", _ => publishes++, () => registry.StructureVersion);
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
