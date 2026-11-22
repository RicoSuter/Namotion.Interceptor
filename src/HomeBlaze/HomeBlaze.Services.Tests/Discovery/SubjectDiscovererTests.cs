using HomeBlaze.Abstractions.Discovery;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class SubjectDiscovererTests
{
    [Fact]
    public async Task WhenAResultMatchesAnExistingSubject_ThenItCarriesThatSubject()
    {
        // Arrange
        var existing = new DiscoveryTestSubject { Address = "a" };
        var otherType = new OtherDiscoveryTestSubject { Address = "b" };
        ISubjectDiscoverer discoverer = new LambdaDiscoverer<DiscoveryTestSubject>(
            (_, _) => TestScans.Yield(new TestDiscoveryResult("a"), new TestDiscoveryResult("b")),
            (subject, result) => subject.Address == result.Key);

        // Act
        var discovered = await discoverer
            .DiscoverAsync(TestScans.CreateScan(), [otherType, existing], CancellationToken.None)
            .ToListAsync();

        // Assert
        Assert.Equal(2, discovered.Count);
        Assert.Same(existing, discovered[0].ExistingSubject);
        Assert.Null(discovered[1].ExistingSubject);
        Assert.All(discovered, item => Assert.Equal(typeof(DiscoveryTestSubject), item.SubjectType));
    }

    [Fact]
    public async Task WhenADeviceIsYieldedTwice_ThenItIsReportedOnce()
    {
        // Arrange
        ISubjectDiscoverer discoverer = new LambdaDiscoverer<DiscoveryTestSubject>(
            (_, _) => TestScans.Yield(new TestDiscoveryResult("a"), new TestDiscoveryResult("a", "Same device again")));

        // Act
        var discovered = await discoverer
            .DiscoverAsync(TestScans.CreateScan(), [], CancellationToken.None)
            .ToListAsync();

        // Assert
        var single = Assert.Single(discovered);
        Assert.Equal("Device", single.Result.DisplayName);
    }
}
