using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Tests.Client;
using Namotion.Interceptor.Testing;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Security.Certificates;

namespace Namotion.Interceptor.OpcUa.Tests;

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

        // Act
        await LoadAsync(application, configuration);

        // Assert
        var certificate = application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate;
        Assert.NotNull(certificate);
        Assert.True(certificate.HasPrivateKey);
        Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs")));
        Assert.InRange(certificate.NotAfter.ToUniversalTime(), DateTime.UtcNow.AddMonths(11), DateTime.UtcNow.AddMonths(13));
    }

    [Fact]
    public async Task WhenLoadedAgain_ThenTheStoredCertificateIsLoaded()
    {
        // Arrange
        var configuration = CreateConfiguration();
        var first = await configuration.CreateApplicationInstanceAsync();
        await LoadAsync(first, configuration);
        var second = await configuration.CreateApplicationInstanceAsync();

        // Act
        await LoadAsync(second, configuration);

        // Assert
        Assert.Equal(GetCertificate(first).Thumbprint, GetCertificate(second).Thumbprint);
        Assert.True(GetCertificate(second).HasPrivateKey);
        Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs")));
    }

    [Fact]
    public async Task WhenAutoCreationIsOffAndNoCertificateExists_ThenLoadingFails()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.AutoCreateApplicationCertificate = false;
        var application = await configuration.CreateApplicationInstanceAsync();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ServiceResultException>(() => LoadAsync(application, configuration));
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
        await LoadAsync(await original.CreateApplicationInstanceAsync(), original);
        var changed = CreateConfiguration();
        changed.ApplicationUri = "urn:new-host:client";

        // Act & Assert
        await Assert.ThrowsAsync<ServiceResultException>(async () => await LoadAsync(await changed.CreateApplicationInstanceAsync(), changed));
        await LoadAsync(await original.CreateApplicationInstanceAsync(), original);
    }

    [Fact]
    public async Task WhenASelfSignedCertificateIsCloseToExpiry_ThenAWarningIsLoggedAndItIsKept()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:test:client";
        using var expiring = await AddSelfSignedCertificateAsync(configuration, ageInDays: 355);
        var application = await configuration.CreateApplicationInstanceAsync();
        var logger = new RecordingLogger();

        // Act
        await LoadAsync(application, configuration, logger);

        // Assert
        Assert.Equal(expiring.Thumbprint, GetCertificate(application).Thumbprint);
        Assert.True(GetCertificate(application).HasPrivateKey);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(expiring.Thumbprint, warning);
        Assert.Contains(Path.Combine(_storePath, "own"), warning);
        Assert.Contains("once it has expired", warning);
    }

    [Fact]
    public async Task WhenACertificateIsNotCloseToExpiry_ThenNoWarningIsLogged()
    {
        // Arrange
        var configuration = CreateConfiguration();
        var application = await configuration.CreateApplicationInstanceAsync();
        var logger = new RecordingLogger();

        // Act
        await LoadAsync(application, configuration, logger);

        // Assert
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task WhenASelfSignedCertificateHasExpired_ThenItIsReplacedWithAWarning()
    {
        // Arrange - the SDK loads an expired certificate of its own as valid, so only the loader replaces it.
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:test:client";
        using var expired = await AddSelfSignedCertificateAsync(configuration, ageInDays: 400);
        var application = await configuration.CreateApplicationInstanceAsync();
        var logger = new RecordingLogger();

        // Act
        await LoadAsync(application, configuration, logger);

        // Assert
        var certificate = GetCertificate(application);
        Assert.NotEqual(expired.Thumbprint, certificate.Thumbprint);
        Assert.True(certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddMonths(11));
        Assert.True(certificate.HasPrivateKey);
        Assert.DoesNotContain(expired.Thumbprint, Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs")))));
        Assert.DoesNotContain(expired.Thumbprint, Path.GetFileName(Assert.Single(Directory.GetFiles(PrivatePath))));
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(expired.Thumbprint, warning);
        Assert.Contains(certificate.Thumbprint, warning);
    }

    [Fact]
    public async Task WhenAutoCreationIsOffAndTheCertificateHasExpired_ThenItIsKeptWithAWarning()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:test:client";
        configuration.AutoCreateApplicationCertificate = false;
        using var expired = await AddSelfSignedCertificateAsync(configuration, ageInDays: 400);
        var application = await configuration.CreateApplicationInstanceAsync();
        var logger = new RecordingLogger();

        // Act
        await LoadAsync(application, configuration, logger);

        // Assert
        Assert.Equal(expired.Thumbprint, GetCertificate(application).Thumbprint);
        Assert.Contains(expired.Thumbprint, Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs")))));
        Assert.Contains("not replaced", Assert.Single(logger.Warnings));
    }

    [Fact]
    public async Task WhenACaIssuedCertificateHasExpired_ThenItIsKeptWithAWarning()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:test:client";
        using var issued = await AddCaIssuedCertificateAsync(configuration, ageInDays: 400);
        var application = await configuration.CreateApplicationInstanceAsync();
        var logger = new RecordingLogger();

        // Act
        await LoadAsync(application, configuration, logger);

        // Assert
        Assert.Equal(issued.Thumbprint, GetCertificate(application).Thumbprint);
        Assert.Contains(issued.Thumbprint, Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs")))));
        Assert.Contains("not replaced", Assert.Single(logger.Warnings));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenACertificateThatIsNotReplacedIsCloseToExpiry_ThenAWarningAsksForANewOne(bool isCaIssued)
    {
        // Arrange - a CA-issued certificate, or a self-signed one with automatic creation off.
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:test:client";
        configuration.AutoCreateApplicationCertificate = isCaIssued;
        using var expiring = isCaIssued
            ? await AddCaIssuedCertificateAsync(configuration, ageInDays: 355)
            : await AddSelfSignedCertificateAsync(configuration, ageInDays: 355);
        var application = await configuration.CreateApplicationInstanceAsync();
        var logger = new RecordingLogger();

        // Act
        await LoadAsync(application, configuration, logger);

        // Assert
        Assert.Equal(expiring.Thumbprint, GetCertificate(application).Thumbprint);
        Assert.Contains(expiring.Thumbprint, Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs")))));
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(expiring.Thumbprint, warning);
        Assert.Contains("Provide a new certificate before then", warning);
    }

    [Fact]
    public async Task WhenTheStoreHoldsTwoExpiredSelfSignedCertificates_ThenBothAreReplacedByOne()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:test:client";
        using var first = await AddSelfSignedCertificateAsync(configuration, ageInDays: 400);
        using var second = await AddSelfSignedCertificateAsync(configuration, ageInDays: 380);
        var application = await configuration.CreateApplicationInstanceAsync();
        var logger = new RecordingLogger();

        // Act
        await LoadAsync(application, configuration, logger);

        // Assert
        var certificate = GetCertificate(application);
        Assert.True(certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddMonths(11));
        var file = Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs"))));
        Assert.Contains(certificate.Thumbprint, file);
        Assert.Equal(2, logger.Warnings.Count);
    }

    [Fact]
    public async Task WhenReplacingAnExpiredCertificateFails_ThenLoadingFails()
    {
        // Arrange - a private key folder that cannot be written makes deleting the expired key fail.
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:test:client";
        using var expired = await AddSelfSignedCertificateAsync(configuration, ageInDays: 400);
        var application = await configuration.CreateApplicationInstanceAsync();

        // Act
        Exception? exception = null;
        if (OperatingSystem.IsWindows() || !await WithReadOnlyAsync(PrivatePath,
            async () => exception = await Record.ExceptionAsync(() => LoadAsync(application, configuration))))
        {
            return;
        }

        // Assert
        Assert.IsType<UnauthorizedAccessException>(exception);
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
        await LoadAsync(application, configuration);

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
            Task.Run(() => LoadAsync(first, configuration)),
            Task.Run(() => LoadAsync(second, configuration)));

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
        await AddToStoreAsync("own", certificate, "secret".ToCharArray());
        var application = await configuration.CreateApplicationInstanceAsync();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ServiceResultException>(() => LoadAsync(application, configuration));
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
        await AddToStoreAsync("own", certificate, "secret".ToCharArray());
        var application = await configuration.CreateApplicationInstanceAsync();

        // Act
        await LoadAsync(application, configuration);

        // Assert
        Assert.True(application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate!.HasPrivateKey);
    }

    private OpcUaClientConfiguration CreateConfiguration() =>
        ClientSourceTestFactory.CreateConfiguration(certificateStoreBasePath: _storePath);

    private string PrivatePath => Path.Combine(_storePath, "own", "private");

    private static Task LoadAsync(ApplicationInstance application, OpcUaClientConfiguration configuration, ILogger? logger = null) =>
        ApplicationCertificateLoader.EnsureLoadedAsync(
            application,
            configuration.AutoCreateApplicationCertificate,
            configuration.ApplicationCertificateLifetimeInMonths,
            configuration.ResolvedTelemetryContext,
            logger ?? NullLogger.Instance,
            CancellationToken.None);

    private static X509Certificate2 GetCertificate(ApplicationInstance application) =>
        application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate!;

    /// <summary>
    /// Adds a self-signed certificate valid for 365 days that was issued <paramref name="ageInDays"/> ago.
    /// </summary>
    private async Task<X509Certificate2> AddSelfSignedCertificateAsync(OpcUaClientConfiguration configuration, int ageInDays)
    {
        var certificate = CertificateFactory
            .CreateCertificate(configuration.ApplicationUri, configuration.ApplicationName, $"CN={configuration.ApplicationName}, O=Namotion", null)
            .SetNotBefore(DateTime.UtcNow.AddDays(-ageInDays))
            .SetLifeTime(TimeSpan.FromDays(365))
            .CreateForRSA();
        await AddToStoreAsync("own", certificate);
        return certificate;
    }

    /// <summary>
    /// Adds a certificate valid for 365 days, issued <paramref name="ageInDays"/> ago by an authority whose certificate
    /// is in the issuer store, which completes the chain so the SDK accepts the certificate on load.
    /// </summary>
    private async Task<X509Certificate2> AddCaIssuedCertificateAsync(OpcUaClientConfiguration configuration, int ageInDays)
    {
        using var authority = CertificateBuilder.Create("CN=Test Authority")
            .SetNotBefore(DateTime.UtcNow.AddDays(-500))
            .SetLifeTime(TimeSpan.FromDays(1000))
            .SetCAConstraint()
            .CreateForRSA();
        await AddToStoreAsync("issuer", authority);
        var certificate = CertificateFactory
            .CreateCertificate(configuration.ApplicationUri, configuration.ApplicationName, $"CN={configuration.ApplicationName}, O=Namotion", null)
            .SetNotBefore(DateTime.UtcNow.AddDays(-ageInDays))
            .SetLifeTime(TimeSpan.FromDays(365))
            .SetIssuer(authority)
            .CreateForRSA();
        await AddToStoreAsync("own", certificate);
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

    private async Task AddToStoreAsync(string storeName, X509Certificate2 certificate, char[]? password = null)
    {
        using var store = new CertificateStoreIdentifier(Path.Combine(_storePath, storeName), CertificateStoreType.Directory, false)
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
