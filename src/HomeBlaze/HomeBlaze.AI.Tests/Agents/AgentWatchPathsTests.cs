using HomeBlaze.AI.Agents;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class AgentWatchPathsTests
{
    [Theory]
    [InlineData("/Energy", "/Energy", true)]
    [InlineData("/Energy/Battery/Level", "/Energy", true)]
    [InlineData("/Energy[0]/Level", "/Energy", true)]
    [InlineData("/EnergyMeter/Power", "/Energy", false)]
    [InlineData("/Anything", "/", true)]
    public void WhenMatchingAPropertyPath_ThenOnlyTheWatchedSubtreeMatches(string path, string watchPath, bool expected)
    {
        // Act
        var matches = AgentWatchPaths.Matches(path, AgentWatchPaths.Normalize([watchPath]));

        // Assert
        Assert.Equal(expected, matches);
    }

    [Theory]
    [InlineData("/", "Energy", "/Energy", true)]
    [InlineData("/Energy", "Level", "/Energy", true)]
    [InlineData("/Energy/Battery", "Level", "/Energy", true)]
    [InlineData("/Energy[0]", "Level", "/Energy", true)]
    [InlineData("/EnergyMeter", "Power", "/Energy", false)]
    [InlineData("/", "EnergyMeter", "/Energy", false)]
    [InlineData("/Energy", "Level", "/Energy/Level", true)]
    [InlineData("/Energy", "LevelMax", "/Energy/Level", false)]
    [InlineData("/Energy", "Level", "/Energy/Level/Extra", false)]
    [InlineData("/Kitchen", "Temperature", "/", true)]
    public void WhenMatchingASubjectPathAndPropertyName_ThenItMatchesLikeTheJoinedPath(string subjectPath, string propertyName, string watchPath, bool expected)
    {
        // Act
        var matches = AgentWatchPaths.Matches(subjectPath, propertyName, AgentWatchPaths.Normalize([watchPath]));

        // Assert
        Assert.Equal(expected, matches);
    }

    [Fact]
    public void WhenNormalizing_ThenBlankEntriesAreDroppedAndSlashesFixed()
    {
        // Act
        var normalized = AgentWatchPaths.Normalize(["Energy/", " ", "/Kitchen/Freezer"]);

        // Assert
        Assert.Equal(["/Energy", "/Kitchen/Freezer"], normalized);
    }

    [Theory]
    [InlineData("/Agents/EnergyAdvisor.json", "Agents_EnergyAdvisor.json")]
    [InlineData("/", "root")]
    [InlineData("/Agents/A:B", "Agents_A_B")]
    public void WhenDerivingAStorageName_ThenItIsAFlatFileName(string path, string expected)
    {
        // Act
        var name = AgentWatchPaths.GetStorageName(path);

        // Assert
        Assert.Equal(expected, name);
    }
}
