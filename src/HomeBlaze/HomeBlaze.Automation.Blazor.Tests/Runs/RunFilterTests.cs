using HomeBlaze.Automation.Blazor.Runs;

namespace HomeBlaze.Automation.Blazor.Tests.Runs;

public class RunFilterTests
{
    private static readonly RunEntry Evening = new(DateTimeOffset.UnixEpoch, "Evening", AutomationRunOutcome.Succeeded, null);
    private static readonly RunEntry Morning = new(DateTimeOffset.UnixEpoch, "Morning", AutomationRunOutcome.Succeeded, null);

    [Fact]
    public void WhenNoRuleIsSelected_ThenAllRunsAreShown()
    {
        // Act & Assert
        Assert.Equal([Evening, Morning], RunFilter.Apply([Evening, Morning], selectedRuleName: null));
    }

    [Fact]
    public void WhenARuleIsSelected_ThenOnlyItsRunsAreShown()
    {
        // Act & Assert
        Assert.Equal([Morning], RunFilter.Apply([Evening, Morning], selectedRuleName: "Morning"));
    }

    [Fact]
    public void WhenTheSelectedRuleHasNoRunsAnymore_ThenNoRunsAreShown()
    {
        // Act & Assert
        Assert.Empty(RunFilter.Apply([Evening], selectedRuleName: "Morning"));
    }
}
