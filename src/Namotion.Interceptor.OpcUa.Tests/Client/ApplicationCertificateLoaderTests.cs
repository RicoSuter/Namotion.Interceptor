using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Security.Certificates;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class ApplicationCertificateLoaderTests : IDisposable
{
    private readonly string _storePath = Path.Combine(Path.GetTempPath(), "opcua-certificate-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storePath))
        {
            Directory.Delete(_storePath, recursive: true);
        }
    }

    [Fact]
    public async Task WhenNoCertificateExists_ThenOneIsCreatedAndLoadedWithItsPrivateKey()
    {
        // Arrange
        var configuration = CreateConfiguration();
        var application = await configuration.CreateApplicationInstanceAsync();
        var loader = new ApplicationCertificateLoader(NullLogger.Instance);

        // Act
        await loader.EnsureLoadedAsync(application, configuration, CancellationToken.None);

        // Assert
        var certificate = application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate;
        Assert.NotNull(certificate);
        Assert.True(certificate.HasPrivateKey);
        Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs")));
        Assert.InRange(certificate.NotAfter.ToUniversalTime(), DateTime.UtcNow.AddMonths(11), DateTime.UtcNow.AddMonths(13));
    }

    [Fact]
    public async Task WhenTheSourceConnectsAgain_ThenTheLoadedCertificateIsReused()
    {
        // Arrange
        var configuration = CreateConfiguration();
        var loader = new ApplicationCertificateLoader(NullLogger.Instance);
        var first = await configuration.CreateApplicationInstanceAsync();
        await loader.EnsureLoadedAsync(first, configuration, CancellationToken.None);
        var second = await configuration.CreateApplicationInstanceAsync();

        // Act
        await loader.EnsureLoadedAsync(second, configuration, CancellationToken.None);

        // Assert
        Assert.Same(
            first.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate,
            second.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate);
    }

    [Fact]
    public async Task WhenAutoCreationIsOffAndNoCertificateExists_ThenLoadingFails()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.AutoCreateApplicationCertificate = false;
        var application = await configuration.CreateApplicationInstanceAsync();
        var loader = new ApplicationCertificateLoader(NullLogger.Instance);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ServiceResultException>(() => loader.EnsureLoadedAsync(application, configuration, CancellationToken.None));
        Assert.Contains($"CN={configuration.ApplicationName}, O=Namotion", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(_storePath, "own", "certs"))
            && Directory.GetFiles(Path.Combine(_storePath, "own", "certs")).Length > 0);
    }

    [Fact]
    public async Task WhenTheCertificateCarriesAnotherApplicationUri_ThenLoadingFailsUntilTheUriMatches()
    {
        // Arrange - a host name change otherwise leaves the stored certificate with a stale URI.
        var original = CreateConfiguration();
        original.ApplicationUri = "urn:old-host:client";
        await new ApplicationCertificateLoader(NullLogger.Instance)
            .EnsureLoadedAsync(await original.CreateApplicationInstanceAsync(), original, CancellationToken.None);
        var changed = CreateConfiguration();
        changed.ApplicationUri = "urn:new-host:client";

        // Act & Assert
        await Assert.ThrowsAsync<ServiceResultException>(async () => await new ApplicationCertificateLoader(NullLogger.Instance)
            .EnsureLoadedAsync(await changed.CreateApplicationInstanceAsync(), changed, CancellationToken.None));
        await new ApplicationCertificateLoader(NullLogger.Instance)
            .EnsureLoadedAsync(await original.CreateApplicationInstanceAsync(), original, CancellationToken.None);
    }

    [Fact]
    public async Task WhenASelfCreatedCertificateIsCloseToExpiry_ThenItIsRenewed()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:test:client";
        using var expiring = await AddExpiringCertificateAsync(configuration);
        var application = await configuration.CreateApplicationInstanceAsync();

        // Act
        await new ApplicationCertificateLoader(NullLogger.Instance).EnsureLoadedAsync(application, configuration, CancellationToken.None);

        // Assert
        var certificate = application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate!;
        Assert.NotEqual(expiring.Thumbprint, certificate.Thumbprint);
        Assert.True(certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddMonths(11));
        Assert.True(certificate.HasPrivateKey);
        Assert.DoesNotContain(expiring.Thumbprint, Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs")))));
        Assert.DoesNotContain(expiring.Thumbprint, Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "private")))));
    }

    [Fact]
    public async Task WhenRenewalFails_ThenTheValidCertificateIsKeptAndRenewedOnTheNextAttempt()
    {
        // Arrange - a private key folder that cannot be written makes deleting the old key fail.
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:test:client";
        using var expiring = await AddExpiringCertificateAsync(configuration);
        var loader = new ApplicationCertificateLoader(NullLogger.Instance);
        var first = await configuration.CreateApplicationInstanceAsync();

        // Act
        if (OperatingSystem.IsWindows() || !await WithReadOnlyAsync(PrivatePath,
            () => loader.EnsureLoadedAsync(first, configuration, CancellationToken.None)))
        {
            return;
        }

        var second = await configuration.CreateApplicationInstanceAsync();
        await loader.EnsureLoadedAsync(second, configuration, CancellationToken.None);

        // Assert
        var kept = first.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate!;
        Assert.Equal(expiring.Thumbprint, kept.Thumbprint);
        Assert.True(kept.HasPrivateKey);
        var renewed = second.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate!;
        Assert.NotEqual(expiring.Thumbprint, renewed.Thumbprint);
        Assert.True(renewed.HasPrivateKey);
    }

    [Fact]
    public async Task WhenRenewalFailsForAnExpiredCertificate_ThenLoadingFails()
    {
        // Arrange - the SDK loads its own expired certificate, so it reaches the renewal, whose delete fails.
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:test:client";
        using var expired = await AddExpiringCertificateAsync(configuration, ageInDays: 400);
        var application = await configuration.CreateApplicationInstanceAsync();
        var loader = new ApplicationCertificateLoader(NullLogger.Instance);

        // Act & Assert
        if (!OperatingSystem.IsWindows())
        {
            await WithReadOnlyAsync(PrivatePath,
                () => Assert.ThrowsAsync<UnauthorizedAccessException>(() => loader.EnsureLoadedAsync(application, configuration, CancellationToken.None)));
        }
    }

    [Fact]
    public async Task WhenTheStoreBreaksWhileTheLoadedCertificateIsValid_ThenTheLoadedCertificateIsKept()
    {
        // Arrange - a failed renewal keeps the expiring certificate loaded, then its private key disappears.
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:test:client";
        using var expiring = await AddExpiringCertificateAsync(configuration);
        var loader = new ApplicationCertificateLoader(NullLogger.Instance);
        var initial = await configuration.CreateApplicationInstanceAsync();
        if (OperatingSystem.IsWindows() || !await WithReadOnlyAsync(PrivatePath,
            () => loader.EnsureLoadedAsync(initial, configuration, CancellationToken.None)))
        {
            return;
        }

        File.Delete(Assert.Single(Directory.GetFiles(PrivatePath)));
        var application = await configuration.CreateApplicationInstanceAsync();

        // Act
        await loader.EnsureLoadedAsync(application, configuration, CancellationToken.None);

        // Assert
        var certificate = application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate!;
        Assert.Equal(expiring.Thumbprint, certificate.Thumbprint);
        Assert.True(certificate.HasPrivateKey);
    }

    [Fact]
    public void WhenACertificateIsCaIssued_ThenItIsNeverRenewed()
    {
        // Arrange
        var configuration = CreateConfiguration();
        using var authority = CertificateBuilder.Create("CN=Test Authority").SetCAConstraint().CreateForRSA();
        using var issued = CertificateBuilder.Create("CN=Issued")
            .SetLifeTime(TimeSpan.FromDays(10))
            .SetIssuer(authority)
            .CreateForRSA();

        // Act
        var needsRenewal = ApplicationCertificateLoader.NeedsRenewal(issued, configuration);

        // Assert
        Assert.False(needsRenewal);
    }

    [Fact]
    public void WhenAutoCreationIsOff_ThenAnExpiringSelfSignedCertificateIsNotRenewed()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.AutoCreateApplicationCertificate = false;
        using var expiring = CertificateBuilder.Create("CN=Expiring").SetLifeTime(TimeSpan.FromDays(10)).CreateForRSA();

        // Act
        var needsRenewal = ApplicationCertificateLoader.NeedsRenewal(expiring, configuration);

        // Assert
        Assert.False(needsRenewal);
    }

    [Fact]
    public async Task WhenACertificateIsAlreadyLoaded_ThenItIsNotReplaced()
    {
        // Arrange - set by a CreateApplicationInstanceAsync override, for example from a hardware security module.
        var configuration = CreateConfiguration();
        var application = await configuration.CreateApplicationInstanceAsync();
        using var provided = CertificateBuilder.Create($"CN={configuration.ApplicationName}, O=Namotion").CreateForRSA();
        application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate = provided;

        // Act
        await new ApplicationCertificateLoader(NullLogger.Instance).EnsureLoadedAsync(application, configuration, CancellationToken.None);

        // Assert
        Assert.Same(provided, application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate);
        Assert.False(Directory.Exists(Path.Combine(_storePath, "own", "certs")));
    }

    [Fact]
    public async Task WhenTwoSourcesShareAStore_ThenOneCertificateIsCreated()
    {
        // Arrange
        var configuration = CreateConfiguration();
        var first = await configuration.CreateApplicationInstanceAsync();
        var second = await configuration.CreateApplicationInstanceAsync();

        // Act
        await Task.WhenAll(
            Task.Run(() => new ApplicationCertificateLoader(NullLogger.Instance).EnsureLoadedAsync(first, configuration, CancellationToken.None)),
            Task.Run(() => new ApplicationCertificateLoader(NullLogger.Instance).EnsureLoadedAsync(second, configuration, CancellationToken.None)));

        // Assert
        Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs")));
        Assert.Equal(
            first.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate!.Thumbprint,
            second.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate!.Thumbprint);
    }

    [Fact]
    public async Task WhenTheKeyCannotBeRead_ThenLoadingFailsWithoutReplacingTheCertificate()
    {
        // Arrange - a password-protected key without the password configured.
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:test:client";
        using var certificate = CertificateFactory
            .CreateCertificate("urn:test:client", configuration.ApplicationName, $"CN={configuration.ApplicationName}, O=Namotion", null)
            .CreateForRSA();
        await AddToOwnStoreAsync(certificate, "secret".ToCharArray());
        var application = await configuration.CreateApplicationInstanceAsync();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ServiceResultException>(() => new ApplicationCertificateLoader(NullLogger.Instance)
            .EnsureLoadedAsync(application, configuration, CancellationToken.None));
        Assert.Contains("Cannot access private key", exception.Message);
        var files = Directory.GetFiles(Path.Combine(_storePath, "own", "certs"));
        Assert.Single(files);
        Assert.Contains(certificate.Thumbprint, Path.GetFileName(files[0]));
    }

    [Fact]
    public async Task WhenTheKeyIsPasswordProtected_ThenItLoadsThroughThePasswordProvider()
    {
        // Arrange
        var configuration = new PasswordConfiguration("secret")
        {
            ServerUrl = "opc.tcp://localhost:4840",
            CertificateStoreBasePath = _storePath,
            ApplicationUri = "urn:test:client",
            AutoCreateApplicationCertificate = false,
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(Connectors.DefaultSubjectFactory.Instance)
        };
        using var certificate = CertificateFactory
            .CreateCertificate("urn:test:client", configuration.ApplicationName, $"CN={configuration.ApplicationName}, O=Namotion", null)
            .CreateForRSA();
        await AddToOwnStoreAsync(certificate, "secret".ToCharArray());
        var application = await configuration.CreateApplicationInstanceAsync();

        // Act
        await new ApplicationCertificateLoader(NullLogger.Instance).EnsureLoadedAsync(application, configuration, CancellationToken.None);

        // Assert
        Assert.True(application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate!.HasPrivateKey);
    }

    private OpcUaClientConfiguration CreateConfiguration() =>
        ClientSourceTestFactory.CreateConfiguration(certificateStoreBasePath: _storePath);

    private string PrivatePath => Path.Combine(_storePath, "own", "private");

    private async Task<X509Certificate2> AddExpiringCertificateAsync(OpcUaClientConfiguration configuration, int ageInDays = 355)
    {
        var certificate = CertificateFactory
            .CreateCertificate(configuration.ApplicationUri, configuration.ApplicationName, $"CN={configuration.ApplicationName}, O=Namotion", null)
            .SetNotBefore(DateTime.UtcNow.AddDays(-ageInDays))
            .SetLifeTime(TimeSpan.FromDays(365))
            .CreateForRSA();
        await AddToOwnStoreAsync(certificate);
        return certificate;
    }

    /// <summary>
    /// Runs <paramref name="action"/> while <paramref name="path"/> cannot be written, and restores write access afterwards.
    /// Returns false without running it when the process can still write, for example as root. Permission bits are
    /// Unix only and xUnit 2 has no dynamic skip, so callers return early on Windows or when this returns false.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static async Task<bool> WithReadOnlyAsync(string path, Func<Task> action)
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var probe = Path.Combine(path, "probe");
            try
            {
                await File.WriteAllTextAsync(probe, string.Empty);
                File.Delete(probe);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // Expected: the folder is read-only.
            }

            await action();
            return true;
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private async Task AddToOwnStoreAsync(X509Certificate2 certificate, char[]? password = null)
    {
        using var store = new CertificateStoreIdentifier(Path.Combine(_storePath, "own"), CertificateStoreType.Directory, false)
            .OpenStore(NullTelemetryContext.Instance);
        await store.AddAsync(certificate, password);
    }

    private sealed class PasswordConfiguration(string password) : OpcUaClientConfiguration
    {
        public override async Task<ApplicationInstance> CreateApplicationInstanceAsync()
        {
            var application = await base.CreateApplicationInstanceAsync();
            application.ApplicationConfiguration.SecurityConfiguration.CertificatePasswordProvider =
                new CertificatePasswordProvider(Encoding.UTF8.GetBytes(password));
            return application;
        }
    }
}
