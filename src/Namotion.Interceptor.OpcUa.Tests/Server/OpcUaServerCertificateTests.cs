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

    [Fact]
    public async Task WhenAnOverrideDisablesCertificateCreation_ThenAnExpiredCertificateIsKeptWithAWarning()
    {
        // Arrange
        using var server = CreateServer(out var configuration, new NoCertificateCreationConfiguration());
        var application = await configuration.CreateApplicationInstanceAsync();
        using var expired = await AddSelfSignedCertificateAsync(application, ageInDays: 400);

        // Act
        await server.LoadApplicationCertificateAsync(application, CancellationToken.None);

        // Assert
        Assert.Equal(expired.Thumbprint, GetCertificate(application).Thumbprint);
        Assert.Contains(expired.Thumbprint, Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs")))));
        Assert.Contains("not replaced", Assert.Single(_logger.Warnings));
    }

    [Fact]
    public async Task WhenTheServerCertificateCannotBeLoaded_ThenTheFailureIsExplained()
    {
        // Arrange - a certificate for another application URI that is not replaced because automatic creation is off.
        using var server = CreateServer(out var configuration, new NoCertificateCreationConfiguration());
        var application = await configuration.CreateApplicationInstanceAsync();
        using var stale = await AddSelfSignedCertificateAsync(application, ageInDays: 10, applicationUri: "urn:old-host:server");
        var identifier = application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.LoadApplicationCertificateAsync(application, CancellationToken.None));
        var inner = Assert.IsType<ServiceResultException>(exception.InnerException);
        Assert.Contains(inner.Message.TrimEnd().TrimEnd('.'), exception.Message);
        Assert.Contains($"'{Path.Combine(identifier.StorePath, "certs")}'", exception.Message);
        Assert.Contains(identifier.SubjectName, exception.Message);
        Assert.Contains("'urn:test:server'", exception.Message);
        Assert.Contains(configuration.BaseAddress, exception.Message);
        Assert.Contains("Automatic certificate creation is off", exception.Message);
    }

    [Fact]
    public async Task WhenLoadingTheServerCertificateIsCanceled_ThenTheCancellationIsNotWrapped()
    {
        // Arrange
        using var server = CreateServer(out var configuration);
        var application = await configuration.CreateApplicationInstanceAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => server.LoadApplicationCertificateAsync(application, cancellation.Token));
    }

    private OpcUaSubjectServer CreateServer(out OpcUaServerConfiguration configuration, OpcUaServerConfiguration? baseConfiguration = null)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        configuration = baseConfiguration ?? new OpcUaServerConfiguration();
        configuration.ApplicationUri = "urn:test:server";
        configuration.CertificateStoreBasePath = _storePath;

        return new OpcUaSubjectServer(new DeliveryRuleTestRoot(context), configuration, _logger);
    }

    private static X509Certificate2 GetCertificate(ApplicationInstance application) =>
        application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate!;

    /// <summary>
    /// Adds a self-signed certificate for the server's host names, valid for 365 days and issued <paramref name="ageInDays"/> ago,
    /// for <paramref name="applicationUri"/> or else the server's application URI.
    /// </summary>
    private async Task<X509Certificate2> AddSelfSignedCertificateAsync(ApplicationInstance application, int ageInDays, string? applicationUri = null)
    {
        var applicationConfiguration = application.ApplicationConfiguration;
        var certificate = CertificateFactory
            .CreateCertificate(
                applicationUri ?? applicationConfiguration.ApplicationUri,
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

    private sealed class NoCertificateCreationConfiguration : OpcUaServerConfiguration
    {
        public override async Task<ApplicationInstance> CreateApplicationInstanceAsync()
        {
            var application = await base.CreateApplicationInstanceAsync();
            application.DisableCertificateAutoCreation = true;
            return application;
        }
    }
}
