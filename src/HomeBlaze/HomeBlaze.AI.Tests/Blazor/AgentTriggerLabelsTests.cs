using HomeBlaze.AI.Blazor;
using Xunit;

namespace HomeBlaze.AI.Tests.Blazor;

public class AgentTriggerLabelsTests
{
    [Theory]
    [InlineData("poll", "Poll interval")]
    [InlineData("change", "Watched change")]
    [InlineData("manual", "Run now")]
    [InlineData("other", "other")]
    [InlineData(null, "")]
    public void WhenFormattingATrigger_ThenTheLabelNamesWhatStartedTheRun(string? trigger, string expected)
    {
        // Act
        var label = AgentTriggerLabels.Format(trigger);

        // Assert
        Assert.Equal(expected, label);
    }
}
