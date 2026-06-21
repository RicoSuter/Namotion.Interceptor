using Xunit;
using Namotion.Interceptor.ConnectorTester.Configuration;

namespace Namotion.Interceptor.ConnectorTester.Tests.Configuration;

public class ChaosConfigurationTests
{
    [Theory]
    [InlineData("kill")]
    [InlineData("disconnect")]
    [InlineData("both")]
    [InlineData("Kill")]
    [InlineData("BOTH")]
    public void WhenModeIsValid_ThenValidatePasses(string mode)
    {
        // Arrange
        var configuration = new ChaosConfiguration { Mode = mode };

        // Act & Assert (no exception)
        configuration.Validate();
    }

    [Theory]
    [InlineData("kkill")]
    [InlineData("dconnect")]
    [InlineData("")]
    [InlineData("random")]
    public void WhenModeIsInvalid_ThenValidateThrows(string mode)
    {
        // Arrange
        var configuration = new ChaosConfiguration { Mode = mode };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }
}
