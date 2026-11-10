using HomeBlaze.Automation.Blazor.Diagram;
using HomeBlaze.Automation.Blazor.Tests.Testing;

namespace HomeBlaze.Automation.Blazor.Tests.Diagram;

public class StateDiagramLayoutCacheTests
{
    private static StateDiagramRule Rule(string name, string? inState, string target) =>
        StateDiagramRule.From(name.ToLowerInvariant(), new AutomationRule { Name = name, InState = inState, Actions = [new ChangeStateAction { Id = "state-1", TargetState = target }] });

    [Fact]
    public void WhenTheStatesAndRulesAreTheSame_ThenTheLayoutIsReused()
    {
        // Arrange
        var cache = new StateDiagramLayoutCache();
        var first = cache.Get(["Idle", "Running"], "Idle", [Rule("Started", "Idle", "Running")]);

        // Act
        var second = cache.Get(["Idle", "Running"], "Idle", [Rule("Started", "Idle", "Running")]);

        // Assert
        Assert.Same(first, second);
    }

    [Theory]
    [InlineData("Start", "Idle", "Running", "Idle")]
    [InlineData("Started", null, "Running", "Idle")]
    [InlineData("Started", "Idle", "Idle", "Idle")]
    [InlineData("Started", "Idle", "Running", "Running")]
    public void WhenARuleOrTheInitialStateChanges_ThenTheLayoutIsComputedAgain(string name, string? inState, string target, string initialState)
    {
        // Arrange
        var cache = new StateDiagramLayoutCache();
        var first = cache.Get(["Idle", "Running"], "Idle", [Rule("Started", "Idle", "Running")]);

        // Act
        var second = cache.Get(["Idle", "Running"], initialState, [Rule(name, inState, target)]);

        // Assert
        Assert.NotSame(first, second);
    }

    [Fact]
    public void WhenTheAutomationAppliesAConfigurationThatDrawsTheSame_ThenTheLayoutIsReused()
    {
        // Arrange
        var automation = TestAutomations.Create(Configure);
        var cache = new StateDiagramLayoutCache();
        var first = cache.Get(automation);
        var again = cache.Get(automation);

        // Act
        Configure(automation);
        var reapplied = cache.Get(automation);
        automation.InitialState = "Running";
        var changed = cache.Get(automation);

        // Assert
        Assert.Same(first, again);
        Assert.Same(first, reapplied);
        Assert.NotSame(first, changed);

        static void Configure(Automation automation)
        {
            automation.States = ["Idle", "Running"];
            automation.InitialState = "Idle";
            automation.Rules = new Dictionary<string, AutomationRule>
            {
                ["started"] = new() { Name = "Started", InState = "Idle", Actions = [new ChangeStateAction { Id = "state-1", TargetState = "Running" }] }
            };
            automation.RuleOrder = ["started"];
        }
    }
}
