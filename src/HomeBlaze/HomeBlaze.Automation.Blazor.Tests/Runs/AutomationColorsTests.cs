using HomeBlaze.Automation.Blazor.Runs;
using MudBlazor;

namespace HomeBlaze.Automation.Blazor.Tests.Runs;

public class AutomationColorsTests
{
    [Theory]
    [InlineData(RuleStatusTexts.Armed, Color.Success)]
    [InlineData(RuleStatusTexts.ArmedWaitingUntilFalse, Color.Success)]
    [InlineData(RuleStatusTexts.Armed + "; not run: Only if is false", Color.Success)]
    [InlineData(RuleStatusTexts.AlreadyTrueWaitingToRearm, Color.Default)]
    [InlineData(RuleStatusTexts.Off, Color.Default)]
    [InlineData(null, Color.Default)]
    public void WhenTheStatusOfARuleIsShown_ThenOnlyAnArmedRuleIsGreen(string? statusText, Color expected)
    {
        // Act
        var color = AutomationColors.GetRuleStatusColor(statusText);

        // Assert
        Assert.Equal(expected, color);
    }
}
