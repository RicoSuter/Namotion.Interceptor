using System.Security.Cryptography.X509Certificates;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Tests.Server;

/// <summary>
/// The certificate step of a server start, which runs without opening a port.
/// </summary>
public class OpcUaServerCertificateTests : IDisposable
{
    private readonly string _storePath = Path.Combine(Path.GetTempPath(), "opcua-certificate-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingLogger _logger = new();

    public void Dispose()
    {
        if (Directory.Exists(_storePath))
        {
            Directory.Delete(_storePath, recursive: true);
        }
    }

    [Fact]
    public async Task WhenTheServerStartsAgain_ThenItKeepsItsCertificate()
    {
        // Arrange
        using var server = CreateServer(out var configuration);
        var first = await configuration.CreateApplicationInstanceAsync();
        await server.LoadApplicationCertificateAsync(first, CancellationToken.None);
        var second = await configuration.CreateApplicationInstanceAsync();

        // Act
        await server.LoadApplicationCertificateAsync(second, CancellationToken.None);

        // Assert
        Assert.Equal(GetCertificate(first).Thumbprint, GetCertificate(second).Thumbprint);
        Assert.True(GetCertificate(second).HasPrivateKey);
        Assert.Empty(_logger.Warnings);
    }

    [Fact]
    public async Task WhenTheServerCertificateIsCloseToExpiry_ThenAWarningIsLoggedAndItIsKept()
    {
        // Arrange
        using var server = CreateServer(out var configuration);
        var application = await configuration.CreateApplicationInstanceAsync();
        using var expiring = await AddSelfSignedCertificateAsync(application, ageInDays: 355);

        // Act
        await server.LoadApplicationCertificateAsync(application, CancellationToken.None);

        // Assert
        Assert.Equal(expiring.Thumbprint, GetCertificate(application).Thumbprint);
        Assert.Contains(expiring.Thumbprint, Assert.Single(_logger.Warnings));
    }

    [Fact]
    public async Task WhenTheServerCertificateHasExpired_ThenItIsReplacedWithAWarning()
    {
        // Arrange
        using var server = CreateServer(out var configuration);
        var application = await configuration.CreateApplicationInstanceAsync();
        using var expired = await AddSelfSignedCertificateAsync(application, ageInDays: 400);

        // Act
        await server.LoadApplicationCertificateAsync(application, CancellationToken.None);

        // Assert
        var certificate = GetCertificate(application);
        Assert.NotEqual(expired.Thumbprint, certificate.Thumbprint);
        Assert.True(certificate.HasPrivateKey);
        Assert.True(certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddMonths(11));
        Assert.DoesNotContain(expired.Thumbprint, Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs")))));
        var warning = Assert.Single(_logger.Warnings);
        Assert.Contains(expired.Thumbprint, warning);
        Assert.Contains(certificate.Thumbprint, warning);
    }

    private OpcUaSubjectServer CreateServer(out OpcUaServerConfiguration configuration)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        configuration = new OpcUaServerConfiguration
        {
            ApplicationUri = "urn:test:server",
            CertificateStoreBasePath = _storePath
        };

        return new OpcUaSubjectServer(new DeliveryRuleTestRoot(context), configuration, _logger);
    }

    private static X509Certificate2 GetCertificate(ApplicationInstance application) =>
        application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate!;

    /// <summary>
    /// Adds a self-signed certificate for the server's host names, valid for 365 days and issued <paramref name="ageInDays"/> ago.
    /// </summary>
    private async Task<X509Certificate2> AddSelfSignedCertificateAsync(ApplicationInstance application, int ageInDays)
    {
        var applicationConfiguration = application.ApplicationConfiguration;
        var certificate = CertificateFactory
            .CreateCertificate(
                applicationConfiguration.ApplicationUri,
                applicationConfiguration.ApplicationName,
                applicationConfiguration.SecurityConfiguration.ApplicationCertificate.SubjectName,
                applicationConfiguration.GetServerDomainNames())
            .SetNotBefore(DateTime.UtcNow.AddDays(-ageInDays))
            .SetLifeTime(TimeSpan.FromDays(365))
            .CreateForRSA();

        using var store = new CertificateStoreIdentifier(Path.Combine(_storePath, "own"), CertificateStoreType.Directory, false)
            .OpenStore(NullTelemetryContext.Instance);
        await store.AddAsync(certificate);
        return certificate;
    }
}
