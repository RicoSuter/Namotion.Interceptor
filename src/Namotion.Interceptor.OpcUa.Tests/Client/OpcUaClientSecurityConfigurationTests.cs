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
    public async Task WhenNoTrustOptionIsSet_ThenTheValidatorRejectsUntrustedServerCertificates()
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
    public async Task WhenUntrustedCertificatesAreAccepted_ThenTheValidatorAcceptsThem()
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
        var configuration = ClientSourceTestFactory.CreateConfiguration();

        // Assert
        Assert.False(configuration.RequireSecurity);
        Assert.True(configuration.AutoCreateApplicationCertificate);
        Assert.Equal((ushort)12, configuration.ApplicationCertificateLifetimeInMonths);
        Assert.Null(configuration.ApplicationUri);
    }
}
