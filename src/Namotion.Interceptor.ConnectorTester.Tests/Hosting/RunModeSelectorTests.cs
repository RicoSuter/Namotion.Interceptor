using Xunit;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Hosting;

namespace Namotion.Interceptor.ConnectorTester.Tests.Hosting;

public class RunModeSelectorTests
{
    private static ConnectorTesterConfiguration MakeConfiguration() =>
        new()
        {
            Server = new() { Name = "server" },
            Clients = [new() { Name = "client-a" }, new() { Name = "client-b" }]
        };

    [Fact]
    public void WhenNoParticipantArg_ThenVerifyMode()
    {
        // Arrange
        var configuration = MakeConfiguration();

        // Act
        var selection = RunModeSelector.Select(args: [], configuration);

        // Assert
        Assert.Equal(RunMode.Verify, selection.Mode);
        Assert.Null(selection.ParticipantName);
    }

    [Fact]
    public void WhenParticipantArgMatchesServer_ThenParticipantMode()
    {
        // Arrange
        var configuration = MakeConfiguration();

        // Act
        var selection = RunModeSelector.Select(args: ["--participant", "server"], configuration);

        // Assert
        Assert.Equal(RunMode.Participant, selection.Mode);
        Assert.Equal("server", selection.ParticipantName);
    }

    [Fact]
    public void WhenParticipantArgMatchesClient_ThenParticipantMode()
    {
        // Arrange
        var configuration = MakeConfiguration();

        // Act
        var selection = RunModeSelector.Select(args: ["--participant", "client-b"], configuration);

        // Assert
        Assert.Equal(RunMode.Participant, selection.Mode);
        Assert.Equal("client-b", selection.ParticipantName);
    }

    [Fact]
    public void WhenParticipantArgUnknown_ThenThrowsWithKnownNamesListed()
    {
        // Arrange
        var configuration = MakeConfiguration();

        // Act
        var exception = Assert.Throws<ArgumentException>(() =>
            RunModeSelector.Select(args: ["--participant", "client-z"], configuration));

        // Assert: error message should help the user pick a valid name.
        Assert.Contains("client-z", exception.Message);
        Assert.Contains("server", exception.Message);
        Assert.Contains("client-a", exception.Message);
        Assert.Contains("client-b", exception.Message);
    }
}
