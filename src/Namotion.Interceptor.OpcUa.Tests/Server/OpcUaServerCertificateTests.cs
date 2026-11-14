using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Security.Certificates;

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
    public async Task WhenTheServerCertificateCarriesAnotherApplicationUri_ThenItIsReplacedWithAWarning()
    {
        // Arrange
        using var server = CreateServer(out var configuration);
        var application = await configuration.CreateApplicationInstanceAsync();
        using var stale = await AddSelfSignedCertificateAsync(application, ageInDays: 10, applicationUri: "urn:old-host:server");

        // Act
        await server.LoadApplicationCertificateAsync(application, CancellationToken.None);

        // Assert
        var certificate = GetCertificate(application);
        Assert.NotEqual(stale.Thumbprint, certificate.Thumbprint);
        Assert.True(certificate.HasPrivateKey);
        Assert.True(X509Utils.CompareApplicationUriWithCertificate(certificate, "urn:test:server"));
        Assert.Contains(certificate.Thumbprint, Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs")))));
        var warning = Assert.Single(_logger.Warnings);
        Assert.Contains(stale.Thumbprint, warning);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains(certificate.Thumbprint));
    }

    [Fact]
    public async Task WhenTheServerCertificateDoesNotCoverTheHost_ThenItIsReplacedWithAWarning()
    {
        // Arrange - issued for the previous host name, such as before a container was recreated.
        using var server = CreateServer(out var configuration);
        var application = await configuration.CreateApplicationInstanceAsync();
        using var stale = await AddSelfSignedCertificateAsync(application, ageInDays: 10, domainNames: ["old-host"]);

        // Act
        await server.LoadApplicationCertificateAsync(application, CancellationToken.None);

        // Assert
        var certificate = GetCertificate(application);
        Assert.NotEqual(stale.Thumbprint, certificate.Thumbprint);
        Assert.True(certificate.HasPrivateKey);
        Assert.Contains(Utils.GetHostName(), X509Utils.GetDomainsFromCertificate(certificate), StringComparer.OrdinalIgnoreCase);
        var warning = Assert.Single(_logger.Warnings);
        Assert.Contains(stale.Thumbprint, warning);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains(certificate.Thumbprint));
        Assert.Contains(Utils.GetHostName(), warning);
    }

    [Fact]
    public async Task WhenACaIssuedServerCertificateCarriesAnotherApplicationUri_ThenItIsKeptAndTheFailureIsExplained()
    {
        // Arrange
        using var server = CreateServer(out var configuration);
        var application = await configuration.CreateApplicationInstanceAsync();
        using var issued = await AddCaIssuedCertificateAsync(application, applicationUri: "urn:old-host:server");
        var identifier = application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.LoadApplicationCertificateAsync(application, CancellationToken.None));
        Assert.IsType<ServiceResultException>(exception.InnerException);
        Assert.Contains($"'{identifier.StorePath}'", exception.Message);
        Assert.Contains("with its private key", exception.Message);
        Assert.Contains(issued.Thumbprint, Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs")))));
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
        Assert.Contains($"'CN={configuration.ApplicationName}, O=Namotion'", exception.Message);
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
    /// for <paramref name="applicationUri"/> and <paramref name="domainNames"/> or else the server's application URI and host names.
    /// </summary>
    private async Task<X509Certificate2> AddSelfSignedCertificateAsync(
        ApplicationInstance application, int ageInDays, string? applicationUri = null, string[]? domainNames = null)
    {
        var applicationConfiguration = application.ApplicationConfiguration;
        var certificate = CertificateFactory
            .CreateCertificate(
                applicationUri ?? applicationConfiguration.ApplicationUri,
                applicationConfiguration.ApplicationName,
                applicationConfiguration.SecurityConfiguration.ApplicationCertificate.SubjectName,
                domainNames ?? applicationConfiguration.GetServerDomainNames())
            .SetNotBefore(DateTime.UtcNow.AddDays(-ageInDays))
            .SetLifeTime(TimeSpan.FromDays(365))
            .CreateForRSA();

        await AddToStoreAsync("own", certificate);
        return certificate;
    }

    /// <summary>
    /// Adds a certificate for the server's host names and <paramref name="applicationUri"/>, issued by an authority whose
    /// certificate is in the issuer store, which completes the chain so the SDK accepts the certificate on load.
    /// </summary>
    private async Task<X509Certificate2> AddCaIssuedCertificateAsync(ApplicationInstance application, string applicationUri)
    {
        using var authority = CertificateBuilder.Create("CN=Test Authority")
            .SetNotBefore(DateTime.UtcNow.AddDays(-10))
            .SetLifeTime(TimeSpan.FromDays(1000))
            .SetCAConstraint()
            .CreateForRSA();
        await AddToStoreAsync("issuer", authority);

        var applicationConfiguration = application.ApplicationConfiguration;
        var certificate = CertificateFactory
            .CreateCertificate(
                applicationUri,
                applicationConfiguration.ApplicationName,
                applicationConfiguration.SecurityConfiguration.ApplicationCertificate.SubjectName,
                applicationConfiguration.GetServerDomainNames())
            .SetNotBefore(DateTime.UtcNow.AddDays(-10))
            .SetLifeTime(TimeSpan.FromDays(365))
            .SetIssuer(authority)
            .CreateForRSA();
        await AddToStoreAsync("own", certificate);
        return certificate;
    }

    private async Task AddToStoreAsync(string storeName, X509Certificate2 certificate)
    {
        using var store = new CertificateStoreIdentifier(Path.Combine(_storePath, storeName), CertificateStoreType.Directory, false)
            .OpenStore(NullTelemetryContext.Instance);
        await store.AddAsync(certificate);
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
