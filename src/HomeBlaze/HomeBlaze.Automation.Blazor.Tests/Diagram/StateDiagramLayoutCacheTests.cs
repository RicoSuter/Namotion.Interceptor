using HomeBlaze.Automation.Blazor.Diagram;

namespace HomeBlaze.Automation.Blazor.Tests.Diagram;

public class StateDiagramLayoutCacheTests
{
    private static StateDiagramRule Rule(string name, string? inState, string target) =>
        StateDiagramRule.Create(name.ToLowerInvariant(), name, inState, [new ChangeStateAction { Id = "state-1", TargetState = target }]);

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
}
