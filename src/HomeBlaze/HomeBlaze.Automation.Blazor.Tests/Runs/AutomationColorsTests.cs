using HomeBlaze.Automation.Blazor.Runs;
using MudBlazor;

namespace HomeBlaze.Automation.Blazor.Tests.Runs;

public class AutomationColorsTests
{
    [Theory]
    [InlineData("Armed", Color.Success)]
    [InlineData("Armed, waiting until false", Color.Success)]
    [InlineData("Already true, waiting to rearm", Color.Default)]
    [InlineData("Off", Color.Default)]
    [InlineData(null, Color.Default)]
    public void WhenTheStatusOfARuleIsShown_ThenOnlyAnArmedRuleIsGreen(string? statusText, Color expected)
    {
        // Act
        var color = AutomationColors.GetRuleStatusColor(statusText);

        // Assert
        Assert.Equal(expected, color);
    }
}
