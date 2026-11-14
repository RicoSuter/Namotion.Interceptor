using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Connectors.OpcUa;
using Opc.Ua;
using Opc.Ua.Bindings;
using Xunit;

namespace Namotion.Interceptor.ConnectorTester.Tests.Connectors.OpcUa;

public class OpcUaConnectorFactoryTests
{
    private const string CertificateStoreRootPath = "/tester/pki";

    [Fact]
    public void WhenSecurityModeIsNone_ThenClientKeepsLibraryDefaults()
    {
        // Arrange
        var connectorFactory = new OpcUaConnectorFactory(new OpcUaConfiguration(), CertificateStoreRootPath);
        var participantConfiguration = new ParticipantConfiguration { Name = "client-a" };

        // Act
        var configuration = connectorFactory.CreateClientConfiguration(participantConfiguration, 4840, NullLoggerFactory.Instance);

        // Assert
        Assert.Equal(MessageSecurityMode.None, configuration.MinimumSecurityMode);
        Assert.False(configuration.AutoAcceptUntrustedCertificates);
        Assert.Equal("pki", configuration.CertificateStoreBasePath);
        Assert.Null(configuration.SessionFactory);
        Assert.Null(Assert.IsType<ConnectorTesterOpcUaClientConfiguration>(configuration).SecurityTokenLifetime);
    }

    [Fact]
    public void WhenSecurityModeIsNone_ThenServerKeepsLibraryDefaultCertificateStore()
    {
        // Arrange
        var connectorFactory = new OpcUaConnectorFactory(new OpcUaConfiguration(), CertificateStoreRootPath);
        var participantConfiguration = new ParticipantConfiguration { Name = "server" };

        // Act
        var configuration = connectorFactory.CreateServerConfiguration(participantConfiguration, NullLoggerFactory.Instance);

        // Assert
        Assert.Equal("pki", configuration.CertificateStoreBasePath);
        Assert.True(configuration.AutoAcceptUntrustedCertificates);
        Assert.False(configuration.CleanCertificateStore);
    }

    [Fact]
    public void WhenSecurityModeIsSignAndEncrypt_ThenClientRequiresItAndAcceptsServerCertificate()
    {
        // Arrange
        var connectorFactory = CreateSecuredFactory();
        var participantConfiguration = new ParticipantConfiguration { Name = "client-a" };

        // Act
        var configuration = connectorFactory.CreateClientConfiguration(participantConfiguration, 4840, NullLoggerFactory.Instance);

        // Assert
        Assert.Equal(MessageSecurityMode.SignAndEncrypt, configuration.MinimumSecurityMode);
        Assert.True(configuration.AutoAcceptUntrustedCertificates);
        Assert.IsType<EndpointLoggingSessionFactory>(configuration.SessionFactory);
        Assert.Equal(
            TimeSpan.FromMilliseconds(TcpMessageLimits.MinSecurityTokenLifeTime),
            Assert.IsType<ConnectorTesterOpcUaClientConfiguration>(configuration).SecurityTokenLifetime);
    }

    [Fact]
    public void WhenSecured_ThenEachParticipantGetsItsOwnCertificateStore()
    {
        // Arrange
        var connectorFactory = CreateSecuredFactory();

        // Act
        var serverConfiguration = connectorFactory.CreateServerConfiguration(
            new ParticipantConfiguration { Name = "server" }, NullLoggerFactory.Instance);
        var clientAConfiguration = connectorFactory.CreateClientConfiguration(
            new ParticipantConfiguration { Name = "client-a" }, 4840, NullLoggerFactory.Instance);
        var clientBConfiguration = connectorFactory.CreateClientConfiguration(
            new ParticipantConfiguration { Name = "client-b" }, 4840, NullLoggerFactory.Instance);

        // Assert
        Assert.Equal(Path.Combine(CertificateStoreRootPath, "server"), serverConfiguration.CertificateStoreBasePath);
        Assert.Equal(Path.Combine(CertificateStoreRootPath, "client-a"), clientAConfiguration.CertificateStoreBasePath);
        Assert.Equal(Path.Combine(CertificateStoreRootPath, "client-b"), clientBConfiguration.CertificateStoreBasePath);
    }

    [Fact]
    public async Task WhenSecurityTokenLifetimeIsSet_ThenApplicationConfigurationRequestsIt()
    {
        // Arrange
        var certificateStoreBasePath = Path.Combine(Path.GetTempPath(), $"connector-tester-{Guid.NewGuid():N}");
        var configuration = new ConnectorTesterOpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            CertificateStoreBasePath = certificateStoreBasePath,
            SecurityTokenLifetime = TimeSpan.FromMinutes(1)
        };

        try
        {
            // Act
            var application = await configuration.CreateApplicationInstanceAsync();

            // Assert
            Assert.Equal(60_000, application.ApplicationConfiguration.TransportQuotas.SecurityTokenLifetime);
        }
        finally
        {
            if (Directory.Exists(certificateStoreBasePath))
            {
                Directory.Delete(certificateStoreBasePath, recursive: true);
            }
        }
    }

    private static OpcUaConnectorFactory CreateSecuredFactory()
        => new(new OpcUaConfiguration { MinimumSecurityMode = MessageSecurityMode.SignAndEncrypt }, CertificateStoreRootPath);
}
