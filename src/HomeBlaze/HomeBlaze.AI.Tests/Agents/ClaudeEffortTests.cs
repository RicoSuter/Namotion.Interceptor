using HomeBlaze.AI.Agents;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class ClaudeEffortTests
{
    [Theory]
    [InlineData(AgentEffort.Low, "low")]
    [InlineData(AgentEffort.Medium, "medium")]
    [InlineData(AgentEffort.High, "high")]
    [InlineData(AgentEffort.ExtraHigh, "xhigh")]
    [InlineData(AgentEffort.Max, "max")]
    public void WhenEffortIsConvertedToTheWireValue_ThenItMatchesTheApiName(AgentEffort effort, string expected)
    {
        // Act
        var value = ClaudeEffort.ToWireValue(effort);

        // Assert
        Assert.Equal(expected, value);
    }
}
