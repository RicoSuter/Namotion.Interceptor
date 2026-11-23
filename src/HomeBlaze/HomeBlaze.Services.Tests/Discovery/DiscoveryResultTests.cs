using HomeBlaze.Abstractions.Discovery;
using Namotion.Interceptor;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class DiscoveryResultTests
{
    [Fact]
    public void WhenTheSubjectHasTheTypeOfTheResult_ThenTheTypedMatchesDecides()
    {
        // Arrange
        IDiscoveryResult result = new AddressDiscoveryResult("a");

        // Act & Assert
        Assert.True(result.Matches(new DiscoveryTestSubject { Address = "a" }));
        Assert.False(result.Matches(new DiscoveryTestSubject { Address = "b" }));
    }

    [Fact]
    public void WhenTheSubjectHasAnotherType_ThenItDoesNotMatch()
    {
        // Arrange
        IDiscoveryResult result = new AddressDiscoveryResult("a");

        // Act
        var isMatch = result.Matches(new OtherDiscoveryTestSubject { Address = "a" });

        // Assert
        Assert.False(isMatch);
    }

    [Fact]
    public void WhenCalledThroughTheTypedInterface_ThenTheSubjectTypeSelectsTheOverload()
    {
        // Arrange
        IDiscoveryResult<DiscoveryTestSubject> result = new AddressDiscoveryResult("a");
        IInterceptorSubject untypedSubject = new DiscoveryTestSubject { Address = "a" };

        // Act & Assert
        Assert.True(result.Matches(new DiscoveryTestSubject { Address = "a" }));
        Assert.True(result.Matches(untypedSubject));
        Assert.False(result.Matches(new OtherDiscoveryTestSubject { Address = "a" }));
    }
}
