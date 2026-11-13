using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.OpcUa.Tests.Server;

public class OpcUaServerConfigurationTests
{
    [Fact]
    public void WhenTheServerConfigurationIsCreated_ThenItKeepsItsCertificateStore()
    {
        // Act
        var configuration = new OpcUaServerConfiguration();

        // Assert
        Assert.False(configuration.CleanCertificateStore);
    }

    [Fact]
    public async Task WhenAnApplicationUriIsSet_ThenTheApplicationUsesIt()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration { ApplicationUri = "urn:test:server" };

        // Act
        var application = await configuration.CreateApplicationInstanceAsync();

        // Assert
        Assert.Equal("urn:test:server", application.ApplicationConfiguration.ApplicationUri);
    }

    [Fact]
    public async Task WhenNoApplicationUriIsSet_ThenItIsDerivedFromTheHostAndApplicationName()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration();

        // Act
        var application = await configuration.CreateApplicationInstanceAsync();

        // Assert
        Assert.Null(configuration.ApplicationUri);
        Assert.Equal(
            $"urn:{System.Net.Dns.GetHostName()}:Namotion.Interceptor:{configuration.ApplicationName}",
            application.ApplicationConfiguration.ApplicationUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void WhenTheApplicationUriIsEmpty_ThenValidationFails(string applicationUri)
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration { ApplicationUri = applicationUri };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => configuration.Validate());
        Assert.Equal(nameof(OpcUaServerConfiguration.ApplicationUri), exception.ParamName);
    }

    [Fact]
    public void WhenTheServerIsCreatedWithAnInvalidConfiguration_ThenItThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var subject = new DeliveryRuleTestRoot(context);
        var configuration = new OpcUaServerConfiguration { ApplicationUri = " " };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => new OpcUaSubjectServer(subject, configuration, NullLogger.Instance));
        Assert.Equal(nameof(OpcUaServerConfiguration.ApplicationUri), exception.ParamName);
    }
}
