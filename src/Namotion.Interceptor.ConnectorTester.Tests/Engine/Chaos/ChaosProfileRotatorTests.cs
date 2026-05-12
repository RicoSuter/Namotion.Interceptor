using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine.Chaos;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Chaos;

public class ChaosProfileRotatorTests
{
    private static ChaosEngine MakeEngine(string targetName)
    {
        var coordinator = new Namotion.Interceptor.ConnectorTester.Engine.TestCycleCoordinator();
        return new ChaosEngine(
            targetName,
            new ChaosConfiguration(),
            coordinator,
            target: null,
            NullLogger.Instance);
    }

    [Fact]
    public void WhenNoProfilesConfigured_ThenApplyForCycleReturnsNull()
    {
        // Arrange
        var rotator = new ChaosProfileRotator(new List<ChaosProfileConfiguration>(), new List<ChaosEngine>(), NullLogger.Instance);

        // Act
        var profileName = rotator.ApplyForCycle(1);

        // Assert
        Assert.Null(profileName);
    }

    [Fact]
    public void WhenMultipleProfilesConfigured_ThenSelectionRotatesRoundRobin()
    {
        // Arrange
        var profiles = new List<ChaosProfileConfiguration>
        {
            new() { Name = "no-chaos",  Participants = [] },
            new() { Name = "client-only", Participants = ["client-a"] },
            new() { Name = "all", Participants = ["server", "client-a"] }
        };
        var server = MakeEngine("server");
        var clientA = MakeEngine("client-a");
        var rotator = new ChaosProfileRotator(profiles, [server, clientA], NullLogger.Instance);

        // Act / Assert
        Assert.Equal("no-chaos", rotator.ApplyForCycle(1));
        Assert.Equal("client-only", rotator.ApplyForCycle(2));
        Assert.Equal("all", rotator.ApplyForCycle(3));
        Assert.Equal("no-chaos", rotator.ApplyForCycle(4));
        Assert.Equal("client-only", rotator.ApplyForCycle(5));
    }

    [Fact]
    public void WhenProfileSetsParticipants_ThenOnlyMatchingEnginesAreEnabled()
    {
        // Arrange
        var profiles = new List<ChaosProfileConfiguration>
        {
            new() { Name = "client-only", Participants = ["client-a"] }
        };
        var server = MakeEngine("server");
        var clientA = MakeEngine("client-a");
        var rotator = new ChaosProfileRotator(profiles, [server, clientA], NullLogger.Instance);

        // Act
        rotator.ApplyForCycle(1);

        // Assert
        Assert.False(server.Enabled);
        Assert.True(clientA.Enabled);
    }
}
