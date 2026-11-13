using System.Text.RegularExpressions;
using HomeBlaze.AI.Agents;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class AgentStorageTests
{
    [Theory]
    [InlineData("/Agents/Claude", "Agents_Claude")]
    [InlineData("/Agents/EnergyAdvisor.json", "Agents_EnergyAdvisor.json")]
    [InlineData("/ClaudeCliAgent", "ClaudeCliAgent")]
    [InlineData("/", "_root")]
    public void WhenAPathHasNoUnderscoreOrInvalidCharacter_ThenTheNameIsItsSegmentsJoinedWithUnderscores(string path, string expected)
    {
        // Act
        var name = AgentStorage.GetName(path);

        // Assert
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("/Agents/A_B", "Agents_A_B")]
    [InlineData("/Agents/A:B", "Agents_A_B")]
    public void WhenAPathHasAnUnderscoreOrInvalidCharacter_ThenTheNameEndsWithAHashOfThePath(string path, string readablePart)
    {
        // Act
        var name = AgentStorage.GetName(path);

        // Assert
        Assert.Matches("^" + Regex.Escape(readablePart) + "__[0-9a-f]{8}$", name);
        Assert.Equal(name, AgentStorage.GetName(path));
    }

    [Theory]
    [InlineData("/Agents/A_B", "/Agents_A/B")]
    [InlineData("/Agents/A_B", "/Agents/A/B")]
    [InlineData("/Agents/A:B", "/Agents/A_B")]
    [InlineData("/A_/B", "/A/_B")]
    [InlineData("/", "/root")]
    public void WhenTwoPathsDiffer_ThenTheirNamesDiffer(string firstPath, string secondPath)
    {
        // Act
        var firstName = AgentStorage.GetName(firstPath);
        var secondName = AgentStorage.GetName(secondPath);

        // Assert
        Assert.NotEqual(firstName, secondName);
    }
}
