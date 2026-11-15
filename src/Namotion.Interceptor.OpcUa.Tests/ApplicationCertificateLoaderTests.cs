using System.Runtime.Versioning;
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
        using var certificate = await LoadAsync(application, configuration);

        // Assert
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
        using var firstCertificate = await LoadAsync(first, configuration);
        var second = await configuration.CreateApplicationInstanceAsync();

        // Act
        using var secondCertificate = await LoadAsync(second, configuration);

        // Assert
        Assert.Equal(firstCertificate.Thumbprint, secondCertificate.Thumbprint);
        Assert.True(secondCertificate.HasPrivateKey);
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
    public async Task WhenASelfSignedCertificateCarriesAnotherApplicationUri_ThenItIsReplacedWithAWarning()
    {
        // Arrange - a host name change otherwise leaves the stored certificate with a stale URI.
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:new-host:client";
        using var stale = await AddSelfSignedCertificateAsync(configuration, ageInDays: 10, applicationUri: "urn:old-host:client");
        var application = await configuration.CreateApplicationInstanceAsync();
        var logger = new RecordingLogger();

        // Act
        using var certificate = await LoadAsync(application, configuration, logger);

        // Assert
        Assert.NotEqual(stale.Thumbprint, certificate.Thumbprint);
        Assert.True(certificate.HasPrivateKey);
        Assert.True(X509Utils.CompareApplicationUriWithCertificate(certificate, "urn:new-host:client"));
        Assert.Contains(certificate.Thumbprint, Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs")))));
        Assert.DoesNotContain(stale.Thumbprint, Path.GetFileName(Assert.Single(Directory.GetFiles(PrivatePath))));
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(stale.Thumbprint, warning);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains(certificate.Thumbprint));
        Assert.Contains("urn:old-host:client", warning);
    }

    [Fact]
    public async Task WhenAMatchingAndAStaleCertificateShareTheSubject_ThenTheMatchingOneIsLoaded()
    {
        // Arrange - the SDK takes the first file whose key loads, so the stale one is deleted only when it comes first.
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:new-host:client";
        using var stale = await AddSelfSignedCertificateAsync(configuration, ageInDays: 10, applicationUri: "urn:old-host:client");
        using var matching = await AddSelfSignedCertificateAsync(configuration, ageInDays: 1);
        var application = await configuration.CreateApplicationInstanceAsync();
        var logger = new RecordingLogger();

        // Act
        using var certificate = await LoadAsync(application, configuration, logger);

        // Assert
        Assert.Equal(matching.Thumbprint, certificate.Thumbprint);
        Assert.True(certificate.HasPrivateKey);
        var files = Directory.GetFiles(Path.Combine(_storePath, "own", "certs")).Select(Path.GetFileName).ToArray();
        Assert.Contains(files, file => file!.Contains(matching.Thumbprint));
        Assert.Equal(files.Length == 1, logger.Warnings.Any(warning => warning.Contains(stale.Thumbprint)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenACertificateThatIsNotReplacedCarriesAnotherApplicationUri_ThenItIsKeptAndLoadingFails(bool isCaIssued)
    {
        // Arrange - a CA-issued certificate, or a self-signed one with automatic creation off.
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:new-host:client";
        configuration.AutoCreateApplicationCertificate = isCaIssued;
        using var stale = isCaIssued
            ? await AddCaIssuedCertificateAsync(configuration, ageInDays: 10, applicationUri: "urn:old-host:client")
            : await AddSelfSignedCertificateAsync(configuration, ageInDays: 10, applicationUri: "urn:old-host:client");
        var application = await configuration.CreateApplicationInstanceAsync();

        // Act & Assert
        await Assert.ThrowsAsync<ServiceResultException>(() => LoadAsync(application, configuration));
        Assert.Contains(stale.Thumbprint, Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs")))));
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
        using var certificate = await LoadAsync(application, configuration, logger);

        // Assert
        Assert.Equal(expiring.Thumbprint, certificate.Thumbprint);
        Assert.True(certificate.HasPrivateKey);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(expiring.Thumbprint, warning);
        Assert.Contains(application.ApplicationConfiguration!.SecurityConfiguration.ApplicationCertificate!.StorePath!, warning);
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
        using var certificate = await LoadAsync(application, configuration, logger);

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
        using var certificate = await LoadAsync(application, configuration, logger);

        // Assert
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
        using var certificate = await LoadAsync(application, configuration, logger);

        // Assert
        Assert.Equal(expired.Thumbprint, certificate.Thumbprint);
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
        using var certificate = await LoadAsync(application, configuration, logger);

        // Assert
        Assert.Equal(issued.Thumbprint, certificate.Thumbprint);
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
        using var certificate = await LoadAsync(application, configuration, logger);

        // Assert
        Assert.Equal(expiring.Thumbprint, certificate.Thumbprint);
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
        using var certificate = await LoadAsync(application, configuration, logger);

        // Assert
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
    public async Task WhenACertificateIsProvisionedInTheOwnStore_ThenItIsUsedAndNotReplaced()
    {
        // Arrange - placed there before the first start, for example by a deployment.
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:test:client";
        using var provisioned = await AddSelfSignedCertificateAsync(configuration, ageInDays: 1);
        var application = await configuration.CreateApplicationInstanceAsync();

        // Act
        using var certificate = await LoadAsync(application, configuration);

        // Assert
        Assert.Equal(provisioned.Thumbprint, certificate.Thumbprint);
        Assert.True(certificate.HasPrivateKey);
        Assert.Contains(provisioned.Thumbprint, Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs")))));
    }

    [Fact]
    public async Task WhenTwoSourcesShareAStore_ThenOneCertificateIsCreated()
    {
        // Arrange
        var configuration = CreateConfiguration();
        var first = await configuration.CreateApplicationInstanceAsync();
        var second = await configuration.CreateApplicationInstanceAsync();

        // Act
        var certificates = await Task.WhenAll(
            Task.Run(() => LoadAsync(first, configuration)),
            Task.Run(() => LoadAsync(second, configuration)));

        // Assert
        using var firstCertificate = certificates[0];
        using var secondCertificate = certificates[1];
        Assert.Single(Directory.GetFiles(Path.Combine(_storePath, "own", "certs")));
        Assert.Equal(firstCertificate.Thumbprint, secondCertificate.Thumbprint);
    }

    [Fact]
    public async Task WhenTheKeyCannotBeRead_ThenLoadingFailsWithoutReplacingTheCertificate()
    {
        // Arrange - a password-protected key without the password configured.
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:test:client";
        using var certificate = DefaultCertificateFactory.Instance
            .CreateApplicationCertificate("urn:test:client", configuration.ApplicationName, $"CN={configuration.ApplicationName}, O=Namotion")
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
        var configuration = CreateConfiguration();
        configuration.ApplicationUri = "urn:test:client";
        configuration.AutoCreateApplicationCertificate = false;
        using var certificate = DefaultCertificateFactory.Instance
            .CreateApplicationCertificate("urn:test:client", configuration.ApplicationName, $"CN={configuration.ApplicationName}, O=Namotion")
            .CreateForRSA();
        await AddToStoreAsync("own", certificate, "secret".ToCharArray());
        var application = await configuration.CreateApplicationInstanceAsync();
        application.ApplicationConfiguration!.SecurityConfiguration.CertificatePasswordProvider =
            new CertificatePasswordProvider(Encoding.UTF8.GetBytes("secret"));

        // Act
        using var loaded = await LoadAsync(application, configuration);

        // Assert
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public async Task WhenTheStoreIsCleanedWhileItIsInUse_ThenTheCleanWaitsAndThenDeletesTheStore()
    {
        // Arrange - the lock is taken through another spelling of the same folder, as a second application may configure it.
        var configuration = CreateConfiguration();
        var application = await configuration.CreateApplicationInstanceAsync();
        (await LoadAsync(application, configuration)).Dispose();
        var identifier = application.ApplicationConfiguration!.SecurityConfiguration.ApplicationCertificate!;
        var otherSpelling = new CertificateIdentifier
        {
            StoreType = CertificateStoreType.Directory,
            StorePath = Path.Combine(_storePath, "own") + Path.DirectorySeparatorChar
        };
        var storeLock = ApplicationCertificateLoader.GetStoreLock(otherSpelling);
        await storeLock.WaitAsync();

        // Act
        Task cleaning;
        try
        {
            cleaning = ApplicationCertificateLoader.CleanStoreAsync(identifier, NullLogger.Instance, CancellationToken.None);

            // Assert
            Assert.False(cleaning.IsCompleted);
            Assert.True(Directory.Exists(Path.Combine(_storePath, "own")));
        }
        finally
        {
            storeLock.Release();
        }

        await cleaning.WaitAsync(TestTimeouts.Default);
        Assert.False(Directory.Exists(Path.Combine(_storePath, "own")));
    }

    private OpcUaClientConfiguration CreateConfiguration() =>
        ClientSourceTestFactory.CreateConfiguration(certificateStoreBasePath: _storePath);

    private string PrivatePath => Path.Combine(_storePath, "own", "private");

    private static Task<Certificate> LoadAsync(ApplicationInstance application, OpcUaClientConfiguration configuration, ILogger? logger = null) =>
        ApplicationCertificateLoader.EnsureLoadedAsync(
            application,
            configuration.AutoCreateApplicationCertificate,
            configuration.ApplicationCertificateLifetimeInMonths,
            configuration.ResolvedTelemetryContext,
            logger ?? NullLogger.Instance,
            CancellationToken.None);

    /// <summary>
    /// Adds a self-signed certificate valid for 365 days that was issued <paramref name="ageInDays"/> ago, for
    /// <paramref name="applicationUri"/> or else the configured application URI.
    /// </summary>
    private async Task<Certificate> AddSelfSignedCertificateAsync(OpcUaClientConfiguration configuration, int ageInDays, string? applicationUri = null)
    {
        var certificate = DefaultCertificateFactory.Instance
            .CreateApplicationCertificate(applicationUri ?? configuration.ApplicationUri!, configuration.ApplicationName, $"CN={configuration.ApplicationName}, O=Namotion")
            .SetNotBefore(DateTime.UtcNow.AddDays(-ageInDays))
            .SetLifeTime(TimeSpan.FromDays(365))
            .CreateForRSA();
        await AddToStoreAsync("own", certificate);
        return certificate;
    }

    /// <summary>
    /// Adds a certificate valid for 365 days, issued <paramref name="ageInDays"/> ago by an authority whose certificate
    /// is in the issuer store, which completes the chain so the SDK accepts the certificate on load. It carries
    /// <paramref name="applicationUri"/> or else the configured application URI.
    /// </summary>
    private async Task<Certificate> AddCaIssuedCertificateAsync(OpcUaClientConfiguration configuration, int ageInDays, string? applicationUri = null)
    {
        using var authority = CertificateBuilder.Create("CN=Test Authority")
            .SetNotBefore(DateTime.UtcNow.AddDays(-500))
            .SetLifeTime(TimeSpan.FromDays(1000))
            .SetCAConstraint()
            .CreateForRSA();
        await AddToStoreAsync("issuer", authority);
        var certificate = DefaultCertificateFactory.Instance
            .CreateApplicationCertificate(applicationUri ?? configuration.ApplicationUri!, configuration.ApplicationName, $"CN={configuration.ApplicationName}, O=Namotion")
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

    private async Task AddToStoreAsync(string storeName, Certificate certificate, char[]? password = null)
    {
        using var store = new CertificateStoreIdentifier(Path.Combine(_storePath, storeName), CertificateStoreType.Directory, false)
            .OpenStore(NullTelemetryContext.Instance);
        await store.AddAsync(certificate, password);
    }
}
