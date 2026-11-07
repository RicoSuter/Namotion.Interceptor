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
        Assert.True(AutomationCompiler.TryCompileRule("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")), [], out var rule, out var error), error);
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
}
