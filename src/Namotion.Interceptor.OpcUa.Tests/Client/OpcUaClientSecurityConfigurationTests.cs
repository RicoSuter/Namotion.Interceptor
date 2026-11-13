using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.OpcUa.Server;
using Opc.Ua;

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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void WhenTheCertificateLifetimeIsBelowTwoMonths_ThenValidationFails(ushort lifetimeInMonths)
    {
        // Arrange
        var configuration = ClientSourceTestFactory.CreateConfiguration();
        configuration.ApplicationCertificateLifetimeInMonths = lifetimeInMonths;

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => configuration.Validate());
        Assert.Equal(nameof(OpcUaClientConfiguration.ApplicationCertificateLifetimeInMonths), exception.ParamName);
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
        var exception = Assert.Throws<ArgumentException>(() => configuration.Validate());
        Assert.Equal(nameof(OpcUaClientConfiguration.ApplicationUri), exception.ParamName);
    }

    [Fact]
    public async Task WhenSecurityIsRequiredAndOnlyUnsecuredEndpointsExist_ThenSelectionFails()
    {
        // Arrange
        var application = await ClientSourceTestFactory.CreateConfiguration().CreateApplicationInstanceAsync();
        var endpoints = new EndpointDescriptionCollection { CreateEndpoint(MessageSecurityMode.None, SecurityPolicies.None) };

        // Act & Assert
        var error = Assert.Throws<InvalidOperationException>(() => SessionManager.SelectEndpoint(
            application.ApplicationConfiguration, new Uri("opc.tcp://localhost:4840"), endpoints,
            requireSecurity: true, NullTelemetryContext.Instance));
        Assert.Contains("RequireSecurity", error.Message);
    }

    [Fact]
    public async Task WhenSecurityIsRequiredAndASecuredEndpointExists_ThenItIsSelected()
    {
        // Arrange
        var application = await ClientSourceTestFactory.CreateConfiguration().CreateApplicationInstanceAsync();
        var endpoints = new EndpointDescriptionCollection
        {
            CreateEndpoint(MessageSecurityMode.None, SecurityPolicies.None),
            CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256)
        };

        // Act
        var endpoint = SessionManager.SelectEndpoint(
            application.ApplicationConfiguration, new Uri("opc.tcp://localhost:4840"), endpoints,
            requireSecurity: true, NullTelemetryContext.Instance);

        // Assert
        Assert.Equal(MessageSecurityMode.SignAndEncrypt, endpoint.SecurityMode);
    }

    [Fact]
    public async Task WhenSecurityIsOptionalAndOnlySecuredEndpointsExist_ThenASecuredOneIsSelected()
    {
        // Arrange
        var application = await ClientSourceTestFactory.CreateConfiguration().CreateApplicationInstanceAsync();
        var endpoints = new EndpointDescriptionCollection { CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256) };

        // Act
        var endpoint = SessionManager.SelectEndpoint(
            application.ApplicationConfiguration, new Uri("opc.tcp://localhost:4840"), endpoints,
            requireSecurity: false, NullTelemetryContext.Instance);

        // Assert
        Assert.Equal(MessageSecurityMode.SignAndEncrypt, endpoint.SecurityMode);
    }

    private static EndpointDescription CreateEndpoint(MessageSecurityMode securityMode, string securityPolicyUri) => new()
    {
        EndpointUrl = "opc.tcp://localhost:4840",
        SecurityMode = securityMode,
        SecurityPolicyUri = securityPolicyUri,
        TransportProfileUri = Profiles.UaTcpTransport
    };
}
