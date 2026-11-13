using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaClientSecurityConfigurationTests
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
    public async Task WhenNoTrustOptionIsSet_ThenTheValidatorDoesNotAutoAcceptUntrustedCertificates()
    {
        // Arrange
        var configuration = ClientSourceTestFactory.CreateConfiguration();

        // Act
        var application = await configuration.CreateApplicationInstanceAsync();

        // Assert
        Assert.False(application.ApplicationConfiguration.SecurityConfiguration.AutoAcceptUntrustedCertificates);
        Assert.False(application.ApplicationConfiguration.CertificateValidator.AutoAcceptUntrustedCertificates);
    }

    [Fact]
    public async Task WhenUntrustedCertificatesAreAccepted_ThenTheValidatorAutoAcceptsThem()
    {
        // Arrange
        var configuration = ClientSourceTestFactory.CreateConfiguration();
        configuration.AutoAcceptUntrustedCertificates = true;

        // Act
        var application = await configuration.CreateApplicationInstanceAsync();

        // Assert
        Assert.True(application.ApplicationConfiguration.CertificateValidator.AutoAcceptUntrustedCertificates);
    }

    [Fact]
    public async Task WhenTheApplicationIsCreated_ThenItHasAnIssuerStoreAndDoesNotTrustItself()
    {
        // Arrange
        var configuration = ClientSourceTestFactory.CreateConfiguration(certificateStoreBasePath: "store");

        // Act
        var application = await configuration.CreateApplicationInstanceAsync();

        // Assert
        var security = application.ApplicationConfiguration.SecurityConfiguration;
        Assert.Equal("store/issuer", security.TrustedIssuerCertificates.StorePath);
        Assert.False(security.AddAppCertToTrustedStore);
    }

    [Fact]
    public async Task WhenAnApplicationUriIsSet_ThenTheApplicationUsesIt()
    {
        // Arrange
        var configuration = ClientSourceTestFactory.CreateConfiguration();
        configuration.ApplicationUri = "urn:test:client";

        // Act
        var application = await configuration.CreateApplicationInstanceAsync();

        // Assert
        Assert.Equal("urn:test:client", application.ApplicationConfiguration.ApplicationUri);
    }

    [Fact]
    public async Task WhenNoApplicationUriIsSet_ThenItIsDerivedFromTheHostAndApplicationName()
    {
        // Arrange
        var configuration = ClientSourceTestFactory.CreateConfiguration();

        // Act
        var application = await configuration.CreateApplicationInstanceAsync();

        // Assert
        Assert.Equal(
            $"urn:{System.Net.Dns.GetHostName()}:Namotion.Interceptor:{configuration.ApplicationName}",
            application.ApplicationConfiguration.ApplicationUri);
    }

    [Fact]
    public void WhenTheClientConfigurationIsCreated_ThenSecurityIsOptionalAndCertificatesAreCreatedForTwelveMonths()
    {
        // Act
        var configuration = new OpcUaClientConfiguration { ServerUrl = "opc.tcp://localhost:4840" };

        // Assert
        Assert.False(configuration.RequireSecurity);
        Assert.True(configuration.AutoCreateApplicationCertificate);
        Assert.Equal((ushort)12, configuration.ApplicationCertificateLifetimeInMonths);
        Assert.Null(configuration.ApplicationUri);
    }

    [Fact]
    public void WhenTheCertificateLifetimeIsZero_ThenValidationFails()
    {
        // Arrange
        var configuration = ClientSourceTestFactory.CreateConfiguration();
        configuration.ApplicationCertificateLifetimeInMonths = 0;

        // Act & Assert
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void WhenTheApplicationUriIsEmpty_ThenValidationFails(string applicationUri)
    {
        // Arrange
        var configuration = ClientSourceTestFactory.CreateConfiguration();
        configuration.ApplicationUri = applicationUri;

        // Act & Assert
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }
}
