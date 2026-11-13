using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.OpcUa.Server;
using Opc.Ua;
using Opc.Ua.Security.Certificates;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaClientSecurityConfigurationTests
{
    private static readonly Uri ServerUri = new("opc.tcp://localhost:4840");

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
        Assert.Equal(MessageSecurityMode.None, configuration.MinimumSecurityMode);
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
        var exception = Assert.Throws<ArgumentException>(() => configuration.Validate());
        Assert.Equal(nameof(OpcUaClientConfiguration.ApplicationCertificateLifetimeInMonths), exception.ParamName);
    }

    [Fact]
    public void WhenTheCertificateLifetimeIsOneMonth_ThenValidationSucceeds()
    {
        // Arrange
        var configuration = ClientSourceTestFactory.CreateConfiguration();
        configuration.ApplicationCertificateLifetimeInMonths = 1;

        // Act
        var exception = Record.Exception(configuration.Validate);

        // Assert
        Assert.Null(exception);
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
    public void WhenTheMinimumSecurityModeIsInvalid_ThenValidationFails()
    {
        // Arrange
        var configuration = ClientSourceTestFactory.CreateConfiguration();
        configuration.MinimumSecurityMode = MessageSecurityMode.Invalid;

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => configuration.Validate());
        Assert.Equal(nameof(OpcUaClientConfiguration.MinimumSecurityMode), exception.ParamName);
    }

    [Theory]
    [InlineData(MessageSecurityMode.Sign)]
    [InlineData(MessageSecurityMode.SignAndEncrypt)]
    public void WhenSecurityIsRequiredAndOnlyUnsecuredEndpointsExist_ThenSelectionFails(MessageSecurityMode minimumSecurityMode)
    {
        // Arrange
        var endpoints = new EndpointDescriptionCollection { CreateEndpoint(MessageSecurityMode.None, SecurityPolicies.None) };

        // Act & Assert
        var error = Assert.Throws<InvalidOperationException>(() => SessionManager.SelectEndpoint(
            CreateApplicationConfiguration(), ServerUri, endpoints, minimumSecurityMode, NullTelemetryContext.Instance));
        Assert.Contains($"no endpoint with the security mode {minimumSecurityMode} or stronger", error.Message);
        Assert.Contains(nameof(OpcUaClientConfiguration.MinimumSecurityMode), error.Message);
        Assert.Contains($"{SecurityPolicies.None} (None)", error.Message);
    }

    [Theory]
    [InlineData(MessageSecurityMode.Sign)]
    [InlineData(MessageSecurityMode.SignAndEncrypt)]
    public void WhenSecurityIsRequiredAndAnEncryptedEndpointExists_ThenItIsSelected(MessageSecurityMode minimumSecurityMode)
    {
        // Arrange
        var endpoints = new EndpointDescriptionCollection
        {
            CreateEndpoint(MessageSecurityMode.None, SecurityPolicies.None),
            CreateEndpoint(MessageSecurityMode.Sign, SecurityPolicies.Basic256Sha256),
            CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256)
        };

        // Act
        var endpoint = SessionManager.SelectEndpoint(
            CreateApplicationConfiguration(), ServerUri, endpoints, minimumSecurityMode, NullTelemetryContext.Instance);

        // Assert
        Assert.Equal(MessageSecurityMode.SignAndEncrypt, endpoint.SecurityMode);
    }

    [Fact]
    public void WhenSigningIsRequiredAndOnlyASignedEndpointExists_ThenItIsSelected()
    {
        // Arrange
        var endpoints = new EndpointDescriptionCollection
        {
            CreateEndpoint(MessageSecurityMode.None, SecurityPolicies.None),
            CreateEndpoint(MessageSecurityMode.Sign, SecurityPolicies.Basic256Sha256)
        };

        // Act
        var endpoint = SessionManager.SelectEndpoint(
            CreateApplicationConfiguration(), ServerUri, endpoints, MessageSecurityMode.Sign, NullTelemetryContext.Instance);

        // Assert
        Assert.Equal(MessageSecurityMode.Sign, endpoint.SecurityMode);
    }

    [Fact]
    public void WhenEncryptionIsRequiredAndOnlyASignedEndpointExists_ThenSelectionFails()
    {
        // Arrange
        var endpoints = new EndpointDescriptionCollection
        {
            CreateEndpoint(MessageSecurityMode.None, SecurityPolicies.None),
            CreateEndpoint(MessageSecurityMode.Sign, SecurityPolicies.Basic256Sha256)
        };

        // Act & Assert
        var error = Assert.Throws<InvalidOperationException>(() => SessionManager.SelectEndpoint(
            CreateApplicationConfiguration(), ServerUri, endpoints, MessageSecurityMode.SignAndEncrypt, NullTelemetryContext.Instance));
        Assert.Contains("no endpoint with the security mode SignAndEncrypt or stronger", error.Message);
        Assert.Contains(nameof(OpcUaClientConfiguration.MinimumSecurityMode), error.Message);
        Assert.Contains($"{SecurityPolicies.Basic256Sha256} (Sign)", error.Message);
    }

    [Fact]
    public void WhenEncryptionIsRequiredAndOnlyTheSignedEndpointHasASupportedPolicy_ThenSelectionFails()
    {
        // Arrange
        var endpoints = new EndpointDescriptionCollection
        {
            CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.ECC_nistP256),
            CreateEndpoint(MessageSecurityMode.Sign, SecurityPolicies.Basic256Sha256)
        };

        // Act & Assert
        var error = Assert.Throws<InvalidOperationException>(() => SessionManager.SelectEndpoint(
            CreateApplicationConfiguration(), ServerUri, endpoints, MessageSecurityMode.SignAndEncrypt, NullTelemetryContext.Instance));
        Assert.Contains("with a security policy this client supports", error.Message);
        Assert.Contains(nameof(OpcUaClientConfiguration.MinimumSecurityMode), error.Message);
        Assert.Contains($"{SecurityPolicies.ECC_nistP256} (SignAndEncrypt)", error.Message);
        Assert.Contains($"{SecurityPolicies.Basic256Sha256} (Sign)", error.Message);
    }

    [Fact]
    public void WhenSecurityIsRequiredAndSecuredEndpointsUseOnlyUnsupportedPolicies_ThenSelectionFails()
    {
        // Arrange
        var endpoints = new EndpointDescriptionCollection
        {
            CreateEndpoint(MessageSecurityMode.None, SecurityPolicies.None),
            CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.ECC_nistP256)
        };

        // Act & Assert
        var error = Assert.Throws<InvalidOperationException>(() => SessionManager.SelectEndpoint(
            CreateApplicationConfiguration(), ServerUri, endpoints, MessageSecurityMode.Sign, NullTelemetryContext.Instance));
        Assert.Contains("with a security policy this client supports", error.Message);
        Assert.Contains(SecurityPolicies.ECC_nistP256, error.Message);
        Assert.Contains(SecurityPolicies.Basic256Sha256, error.Message);
    }

    [Fact]
    public void WhenSecurityIsOptionalAndAnUnsecuredEndpointExists_ThenItIsPreferred()
    {
        // Arrange
        var endpoints = new EndpointDescriptionCollection
        {
            CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256),
            CreateEndpoint(MessageSecurityMode.None, SecurityPolicies.None)
        };

        // Act
        var endpoint = SessionManager.SelectEndpoint(
            CreateApplicationConfiguration(), ServerUri, endpoints, MessageSecurityMode.None, NullTelemetryContext.Instance);

        // Assert
        Assert.Equal(MessageSecurityMode.None, endpoint.SecurityMode);
    }

    [Fact]
    public void WhenSecurityIsOptionalAndOnlySecuredEndpointsExist_ThenASecuredOneIsSelected()
    {
        // Arrange
        var endpoints = new EndpointDescriptionCollection { CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256) };

        // Act
        var endpoint = SessionManager.SelectEndpoint(
            CreateApplicationConfiguration(), ServerUri, endpoints, MessageSecurityMode.None, NullTelemetryContext.Instance);

        // Assert
        Assert.Equal(MessageSecurityMode.SignAndEncrypt, endpoint.SecurityMode);
    }

    [Fact]
    public void WhenSecurityIsOptionalAndTheFirstSecuredEndpointHasAnUnsupportedPolicy_ThenASupportedOneIsSelected()
    {
        // Arrange
        var endpoints = new EndpointDescriptionCollection
        {
            CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.ECC_nistP256),
            CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256)
        };

        // Act
        var endpoint = SessionManager.SelectEndpoint(
            CreateApplicationConfiguration(), ServerUri, endpoints, MessageSecurityMode.None, NullTelemetryContext.Instance);

        // Assert
        Assert.Equal(SecurityPolicies.Basic256Sha256, endpoint.SecurityPolicyUri);
    }

    [Fact]
    public void WhenSecurityIsOptionalAndSecuredEndpointsUseOnlyUnsupportedPolicies_ThenSelectionFails()
    {
        // Arrange
        var endpoints = new EndpointDescriptionCollection { CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.ECC_nistP256) };

        // Act & Assert
        var error = Assert.Throws<InvalidOperationException>(() => SessionManager.SelectEndpoint(
            CreateApplicationConfiguration(), ServerUri, endpoints, MessageSecurityMode.None, NullTelemetryContext.Instance));
        Assert.Contains(SecurityPolicies.ECC_nistP256, error.Message);
        Assert.Contains(SecurityPolicies.Basic256Sha256, error.Message);
    }

    [Fact]
    public void WhenNoEndpointMatchesTheScheme_ThenSelectionFails()
    {
        // Arrange
        var endpoints = new EndpointDescriptionCollection
        {
            CreateEndpoint(MessageSecurityMode.None, SecurityPolicies.None, "https://localhost:4843")
        };

        // Act & Assert
        var error = Assert.Throws<InvalidOperationException>(() => SessionManager.SelectEndpoint(
            CreateApplicationConfiguration(), ServerUri, endpoints, MessageSecurityMode.None, NullTelemetryContext.Instance));
        Assert.Contains("'opc.tcp' scheme", error.Message);
    }

    [Fact]
    public void WhenTheServerCertificateIsUntrusted_ThenTheMessageNamesTheRejectedAndTrustedFoldersAndAutoAccept()
    {
        // Arrange
        using var serverCertificate = CertificateBuilder.Create("CN=Server").CreateForRSA();
        var endpoint = CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256);
        endpoint.ServerCertificate = serverCertificate.RawData;
        var innerException = new ServiceResultException(StatusCodes.BadCertificateUntrusted);

        // Act
        var error = SessionManager.CreateServerCertificateRejectionException(
            innerException, endpoint, CreateApplicationConfiguration(), NullTelemetryContext.Instance);

        // Assert
        Assert.Same(innerException, error.InnerException);
        Assert.Contains(serverCertificate.Thumbprint, error.Message);
        Assert.Contains(Path.Combine("store/rejected", "certs"), error.Message);
        Assert.Contains(Path.Combine("store/trusted", "certs"), error.Message);
        Assert.Contains(nameof(OpcUaClientConfiguration.AutoAcceptUntrustedCertificates), error.Message);
    }

    [Fact]
    public void WhenTheServerCertificateChainIsIncomplete_ThenTheMessageNamesTheIssuerFolderWithoutAutoAccept()
    {
        // Arrange
        using var serverCertificate = CertificateBuilder.Create("CN=Server").CreateForRSA();
        var endpoint = CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256);
        endpoint.ServerCertificate = serverCertificate.RawData;

        // Act
        var error = SessionManager.CreateServerCertificateRejectionException(
            new ServiceResultException(StatusCodes.BadCertificateChainIncomplete), endpoint,
            CreateApplicationConfiguration(), NullTelemetryContext.Instance);

        // Assert
        Assert.Contains(serverCertificate.Thumbprint, error.Message);
        Assert.Contains(Path.Combine("store/issuer", "certs"), error.Message);
        Assert.Contains(Path.Combine("store/trusted", "certs"), error.Message);
        Assert.Contains(Path.Combine("store/rejected", "certs"), error.Message);
        Assert.DoesNotContain(nameof(OpcUaClientConfiguration.AutoAcceptUntrustedCertificates), error.Message);
    }

    [Fact]
    public void WhenTheEndpointHasNoServerCertificate_ThenTheThumbprintIsUnknown()
    {
        // Arrange
        var endpoint = CreateEndpoint(MessageSecurityMode.None, SecurityPolicies.None);

        // Act
        var error = SessionManager.CreateServerCertificateRejectionException(
            new ServiceResultException(StatusCodes.BadCertificateUntrusted), endpoint,
            CreateApplicationConfiguration(), NullTelemetryContext.Instance);

        // Assert
        Assert.Contains("(unknown)", error.Message);
    }

    [Fact]
    public void WhenTheClientHasACertificate_ThenTheRejectionMessageNamesItsThumbprint()
    {
        // Arrange
        using var clientCertificate = CertificateBuilder.Create("CN=Client").CreateForRSA();
        var applicationConfiguration = CreateApplicationConfiguration();
        applicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate = clientCertificate;

        // Act
        var error = SessionManager.CreateServerCertificateRejectionException(
            new ServiceResultException(StatusCodes.BadCertificateUntrusted),
            CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256),
            applicationConfiguration, NullTelemetryContext.Instance);

        // Assert
        Assert.Contains($"this client's certificate {clientCertificate.Thumbprint}", error.Message);
    }

    [Fact]
    public void WhenTheEndpointIsUnsecured_ThenTheRejectionMessageOmitsTheClientCertificateHint()
    {
        // Arrange
        using var clientCertificate = CertificateBuilder.Create("CN=Client").CreateForRSA();
        var applicationConfiguration = CreateApplicationConfiguration();
        applicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate = clientCertificate;

        // Act
        var error = SessionManager.CreateServerCertificateRejectionException(
            new ServiceResultException(StatusCodes.BadCertificateUntrusted),
            CreateEndpoint(MessageSecurityMode.None, SecurityPolicies.None),
            applicationConfiguration, NullTelemetryContext.Instance);

        // Assert
        Assert.DoesNotContain("this client's certificate", error.Message);
        Assert.DoesNotContain(clientCertificate.Thumbprint, error.Message);
    }

    [Fact]
    public void WhenTheRejectedStoreIsNotADirectory_ThenItsPathIsNamedWithoutTheCertsFolder()
    {
        // Arrange
        var applicationConfiguration = CreateApplicationConfiguration();
        applicationConfiguration.SecurityConfiguration.RejectedCertificateStore = new CertificateTrustList
        {
            StoreType = CertificateStoreType.X509Store,
            StorePath = "CurrentUser\\Rejected"
        };

        // Act
        var error = SessionManager.CreateServerCertificateRejectionException(
            new ServiceResultException(StatusCodes.BadCertificateUntrusted),
            CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256),
            applicationConfiguration, NullTelemetryContext.Instance);

        // Assert
        Assert.Contains("'CurrentUser\\Rejected'", error.Message);
    }

    private static ApplicationConfiguration CreateApplicationConfiguration() => new()
    {
        SecurityConfiguration = new SecurityConfiguration
        {
            ApplicationCertificate = new CertificateIdentifier { StoreType = CertificateStoreType.Directory, StorePath = "store/own" },
            TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = "store/trusted" },
            TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = "store/issuer" },
            RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = "store/rejected" }
        }
    };

    private static EndpointDescription CreateEndpoint(
        MessageSecurityMode securityMode, string securityPolicyUri, string endpointUrl = "opc.tcp://localhost:4840") => new()
    {
        EndpointUrl = endpointUrl,
        SecurityMode = securityMode,
        SecurityPolicyUri = securityPolicyUri,
        TransportProfileUri = Profiles.UaTcpTransport
    };
}
