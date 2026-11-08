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
    // The location is resolved with the paths of the rules on a graph change, as the worker does, which binds the version.
    [Fact]
    public async Task WhenAGraphChangeLeavesTheStructureAsItWas_ThenThePathsAreNotResolvedAgain()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var graph = host.Graph;
        var automation = host.CreateAutomation("Gusts");
        graph.Add("Gusts", automation);
        var registry = graph.Context.GetService<ISubjectRegistry>();
        var publishes = 0;
        var routes = new GraphRoutes(graph.Resolver, automation, () => registry.StructureVersion, null);
        var triggers = new AutomationTriggers(
            routes, new FakeTimeProvider(AutomationTestHost.Start), new AutomationInputQueue(), NullLogger.Instance, () => "Gusts", _ => publishes++);
        var rule = Compile("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")));
        rule.IsActivated = true;
        triggers.Activate([rule], [rule]);
        triggers.GetRequests(new ActivationInput([rule], 0), [rule]);
        routes.Publish(0);
        routes.ResolveLocation(automation);
        triggers.OnGraphChanged([rule]);
        routes.Publish(0);
        var publishesAfterTheFirstGraphChange = publishes;

        // Act
        graph.Root.Children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children);
        triggers.OnGraphChanged([rule]);
        routes.Publish(0);
        var publishesAfterTheSameChildren = publishes;
        graph.Add("Blinds", new TestBlinds());
        routes.ResolveLocation(automation);
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
