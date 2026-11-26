using HomeBlaze.Abstractions.Discovery;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class SubjectDiscoveryTests
{
    [Fact]
    public async Task WhenTheScanYieldsResults_ThenTheInterfaceYieldsThemAsTheyAre()
    {
        // Arrange
        var first = new TestDiscoveryResult("a");
        var again = new TestDiscoveryResult("a", "Same device again");
        ISubjectDiscovery discovery = new LambdaDiscovery<DiscoveryTestSubject>(_ => TestScans.Yield(first, again));

        // Act
        var results = await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Assert
        Assert.Equal([first, again], results);
        Assert.Equal(typeof(DiscoveryTestSubject), discovery.SubjectType);
    }
}
