using System.Security.Cryptography.X509Certificates;
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
        var error = SessionManager.CreateCertificateErrorException(
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
        var error = SessionManager.CreateCertificateErrorException(
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
        var error = SessionManager.CreateCertificateErrorException(
            new ServiceResultException(StatusCodes.BadCertificateUntrusted), endpoint,
            CreateApplicationConfiguration(), NullTelemetryContext.Instance);

        // Assert
        Assert.Contains("(unknown)", error.Message);
    }

    [Fact]
    public void WhenTheServerCertificateIsUntrusted_ThenTheMessageDoesNotBlameTheClientCertificate()
    {
        // Arrange
        using var clientCertificate = CertificateBuilder.Create("CN=Client").CreateForRSA();
        var applicationConfiguration = CreateApplicationConfiguration();
        applicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate = clientCertificate;

        // Act
        var error = SessionManager.CreateCertificateErrorException(
            new ServiceResultException(StatusCodes.BadCertificateUntrusted),
            CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256),
            applicationConfiguration, NullTelemetryContext.Instance);

        // Assert
        Assert.DoesNotContain(clientCertificate.Thumbprint, error.Message);
    }

    [Fact]
    public void WhenTheServerRejectsTheSecureChannel_ThenTheMessageNamesTheClientCertificateAndTheOwnFolder()
    {
        // Arrange
        using var clientCertificate = CertificateBuilder.Create("CN=Client").CreateForRSA();
        var applicationConfiguration = CreateApplicationConfiguration();
        applicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate = clientCertificate;
        var innerException = new ServiceResultException(StatusCodes.BadSecurityChecksFailed);

        // Act
        var error = SessionManager.CreateCertificateErrorException(
            innerException,
            CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256),
            applicationConfiguration, NullTelemetryContext.Instance);

        // Assert
        Assert.Same(innerException, error.InnerException);
        Assert.Contains($"does not trust this client's certificate {clientCertificate.Thumbprint}", error.Message);
        Assert.Contains(Path.Combine("store/own", "certs"), error.Message);
        Assert.DoesNotContain(nameof(OpcUaClientConfiguration.AutoAcceptUntrustedCertificates), error.Message);
    }

    [Fact]
    public void WhenTheServerCertificateHasExpired_ThenTheMessageNamesItAndItsValidity()
    {
        // Arrange
        using var serverCertificate = CreateCertificate("CN=Server", notBefore: DateTime.UtcNow.AddDays(-30), notAfter: DateTime.UtcNow.AddDays(-1));
        var endpoint = CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256);
        endpoint.ServerCertificate = serverCertificate.RawData;

        // Act
        var error = SessionManager.CreateCertificateErrorException(
            new ServiceResultException(StatusCodes.BadCertificateTimeInvalid), endpoint,
            CreateApplicationConfiguration(), NullTelemetryContext.Instance);

        // Assert
        Assert.Contains($"The OPC UA server certificate {serverCertificate.Thumbprint} is expired or not yet valid", error.Message);
        Assert.Contains(serverCertificate.NotAfter.ToUniversalTime().ToString("u"), error.Message);
    }

    [Fact]
    public void WhenTheClientCertificateHasExpired_ThenTheMessageNamesItAndTheOwnFolder()
    {
        // Arrange
        using var serverCertificate = CertificateBuilder.Create("CN=Server").CreateForRSA();
        using var clientCertificate = CreateCertificate("CN=Client", notBefore: DateTime.UtcNow.AddDays(-30), notAfter: DateTime.UtcNow.AddDays(-1));
        var endpoint = CreateEndpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256);
        endpoint.ServerCertificate = serverCertificate.RawData;
        var applicationConfiguration = CreateApplicationConfiguration();
        applicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate = clientCertificate;

        // Act
        var error = SessionManager.CreateCertificateErrorException(
            new ServiceResultException(StatusCodes.BadCertificateTimeInvalid), endpoint,
            applicationConfiguration, NullTelemetryContext.Instance);

        // Assert
        Assert.Contains($"this client's certificate {clientCertificate.Thumbprint}", error.Message);
        Assert.Contains(Path.Combine("store/own", "certs"), error.Message);
        Assert.DoesNotContain(serverCertificate.Thumbprint, error.Message);
    }

    [Theory]
    [InlineData(StatusCodes.BadCertificateUntrusted, MessageSecurityMode.None, true)]
    [InlineData(StatusCodes.BadCertificateChainIncomplete, MessageSecurityMode.SignAndEncrypt, true)]
    [InlineData(StatusCodes.BadCertificateTimeInvalid, MessageSecurityMode.None, true)]
    [InlineData(StatusCodes.BadSecurityChecksFailed, MessageSecurityMode.Sign, true)]
    [InlineData(StatusCodes.BadSecurityChecksFailed, MessageSecurityMode.None, false)]
    [InlineData(StatusCodes.BadTimeout, MessageSecurityMode.SignAndEncrypt, false)]
    public void WhenSessionCreationFails_ThenOnlyCertificateErrorsAreExplained(
        uint statusCode, MessageSecurityMode securityMode, bool expected)
    {
        // Arrange
        var endpoint = CreateEndpoint(securityMode, securityMode == MessageSecurityMode.None ? SecurityPolicies.None : SecurityPolicies.Basic256Sha256);

        // Act
        var isCertificateError = SessionManager.IsCertificateError(new ServiceResultException(statusCode), endpoint);

        // Assert
        Assert.Equal(expected, isCertificateError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenTheClientCertificateCannotBeLoaded_ThenTheMessageNamesTheCauseSubjectAndApplicationUri(bool autoCreate)
    {
        // Arrange
        var applicationConfiguration = CreateApplicationConfiguration();
        applicationConfiguration.ApplicationUri = "urn:test:client";
        applicationConfiguration.SecurityConfiguration.ApplicationCertificate.SubjectName = "CN=Client, O=Namotion";
        var innerException = new InvalidOperationException("The private key is missing.");

        // Act
        var error = SessionManager.CreateCertificateLoadException(innerException, applicationConfiguration, autoCreate);

        // Assert
        Assert.Same(innerException, error.InnerException);
        Assert.Contains("The private key is missing.", error.Message);
        Assert.Contains(Path.Combine("store/own", "certs"), error.Message);
        Assert.Contains("'CN=Client, O=Namotion'", error.Message);
        Assert.Contains("'urn:test:client'", error.Message);
        Assert.Equal(autoCreate, error.Message.Contains("delete"));
        Assert.Equal(!autoCreate, error.Message.Contains(nameof(OpcUaClientConfiguration.AutoCreateApplicationCertificate)));
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
        var error = SessionManager.CreateCertificateErrorException(
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

    private static X509Certificate2 CreateCertificate(string subjectName, DateTime notBefore, DateTime notAfter) => CertificateBuilder
        .Create(subjectName)
        .SetNotBefore(notBefore)
        .SetNotAfter(notAfter)
        .CreateForRSA();

    private static EndpointDescription CreateEndpoint(
        MessageSecurityMode securityMode, string securityPolicyUri, string endpointUrl = "opc.tcp://localhost:4840") => new()
    {
        EndpointUrl = endpointUrl,
        SecurityMode = securityMode,
        SecurityPolicyUri = securityPolicyUri,
        TransportProfileUri = Profiles.UaTcpTransport
    };
}
