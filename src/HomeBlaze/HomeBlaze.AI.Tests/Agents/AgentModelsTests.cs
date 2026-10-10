using HomeBlaze.AI.Agents;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class AgentModelsTests
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
        var value = effort.ToWireValue();

        // Assert
        Assert.Equal(expected, value);
    }
}
