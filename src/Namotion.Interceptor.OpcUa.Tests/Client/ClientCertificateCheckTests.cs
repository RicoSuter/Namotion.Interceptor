using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.Testing;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Security.Certificates;
using static Namotion.Interceptor.OpcUa.Tests.Client.ClientSourceTestFactory;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// The managed session keeps the client certificate it loaded, so the health check checks the certificate in the own
/// store while the source runs and hands a replacement to the certificate manager the managed session observes.
/// </summary>
public class ClientCertificateCheckTests : IDisposable
{
    private readonly string _storePath = Path.Combine(Path.GetTempPath(), "opcua-certificate-check-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storePath))
        {
            Directory.Delete(_storePath, recursive: true);
        }
    }

    [Fact]
    public async Task WhenACertificateIsSwappedIntoTheStore_ThenTheNextDueCheckHandsItToTheCertificateManagerOnce()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        var (source, configuration) = CreateSourceAndConfiguration();
        await using var sessionManager = CreateSessionManager(source, configuration, timeProvider);
        var application = await configuration.CreateApplicationInstanceAsync();
        using var currentCertificate = await LoadCertificateAsync(application, configuration);
        AttachApplication(sessionManager, application, currentCertificate, timeProvider);
        var changes = SubscribeToCertificateChanges(application);

        using var swapped = await SwapCertificateInStoreAsync(application, currentCertificate, TimeSpan.FromDays(365));

        // Act
        await sessionManager.CheckClientCertificateIfDueAsync(CancellationToken.None);
        var updatesBeforeDue = CountUpdates(changes);

        timeProvider.Advance(SessionManager.ClientCertificateCheckInterval);
        await sessionManager.CheckClientCertificateIfDueAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, updatesBeforeDue);
        Assert.Equal(swapped.Thumbprint, GetClientCertificateThumbprint(sessionManager));
        var update = Assert.Single(changes, change => change.Kind == CertificateChangeKind.ApplicationCertificateUpdated);
        Assert.Equal(swapped.Thumbprint, update.NewCertificate?.Thumbprint);
    }

    [Fact]
    public async Task WhenTheLoaderReplacesTheCertificate_ThenTheCertificateManagerIsUpdatedOnce()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        var (source, configuration) = CreateSourceAndConfiguration();
        await using var sessionManager = CreateSessionManager(source, configuration, timeProvider);
        var application = await configuration.CreateApplicationInstanceAsync();
        using var currentCertificate = await LoadCertificateAsync(application, configuration);
        var originalThumbprint = currentCertificate.Thumbprint;
        AttachApplication(sessionManager, application, currentCertificate, timeProvider);
        var changes = SubscribeToCertificateChanges(application);

        Directory.Delete(application.ApplicationConfiguration!.SecurityConfiguration.ApplicationCertificate!.StorePath!, recursive: true);

        // Act
        timeProvider.Advance(SessionManager.ClientCertificateCheckInterval);
        await sessionManager.CheckClientCertificateIfDueAsync(CancellationToken.None);

        // Assert
        var replacement = GetClientCertificateThumbprint(sessionManager);
        Assert.NotEqual(originalThumbprint, replacement);
        var update = Assert.Single(changes, change => change.Kind == CertificateChangeKind.ApplicationCertificateUpdated);
        Assert.Equal(replacement, update.NewCertificate?.Thumbprint);
    }

    [Fact]
    public async Task WhenTheCertificateInTheStoreIsUnchanged_ThenTheCertificateManagerIsNotUpdated()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        var (source, configuration) = CreateSourceAndConfiguration();
        await using var sessionManager = CreateSessionManager(source, configuration, timeProvider);
        var application = await configuration.CreateApplicationInstanceAsync();
        using var currentCertificate = await LoadCertificateAsync(application, configuration);
        var originalThumbprint = currentCertificate.Thumbprint;
        AttachApplication(sessionManager, application, currentCertificate, timeProvider);
        var changes = SubscribeToCertificateChanges(application);

        // Act
        timeProvider.Advance(SessionManager.ClientCertificateCheckInterval);
        await sessionManager.CheckClientCertificateIfDueAsync(CancellationToken.None);

        // Assert
        Assert.Equal(originalThumbprint, GetClientCertificateThumbprint(sessionManager));
        Assert.Equal(0, CountUpdates(changes));
    }

    [Fact]
    public async Task WhenAReconnectCycleFailedWithACertificateError_ThenTheNextCheckRunsBeforeTheInterval()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        var (source, configuration) = CreateSourceAndConfiguration();
        await using var sessionManager = CreateSessionManager(source, configuration, timeProvider);
        var application = await configuration.CreateApplicationInstanceAsync();
        using var currentCertificate = await LoadCertificateAsync(application, configuration);
        AttachApplication(sessionManager, application, currentCertificate, timeProvider);
        var changes = SubscribeToCertificateChanges(application);

        using var swapped = await SwapCertificateInStoreAsync(application, currentCertificate, TimeSpan.FromDays(365));
        GetField("_clientCertificateCheckRequested").SetValue(sessionManager, 1);

        // Act
        timeProvider.Advance(SessionManager.RequestedClientCertificateCheckInterval);
        await sessionManager.CheckClientCertificateIfDueAsync(CancellationToken.None);

        // Assert
        Assert.Equal(swapped.Thumbprint, GetClientCertificateThumbprint(sessionManager));
        Assert.Equal(1, CountUpdates(changes));
    }

    [Fact]
    public async Task WhenChecksAreRequestedRepeatedly_ThenTheyRunAtMostOncePerRequestedInterval()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        var (source, configuration) = CreateSourceAndConfiguration();
        await using var sessionManager = CreateSessionManager(source, configuration, timeProvider);
        var application = await configuration.CreateApplicationInstanceAsync();
        using var currentCertificate = await LoadCertificateAsync(application, configuration);
        var originalThumbprint = currentCertificate.Thumbprint;
        AttachApplication(sessionManager, application, currentCertificate, timeProvider);

        using var swapped = await SwapCertificateInStoreAsync(application, currentCertificate, TimeSpan.FromDays(365));
        GetField("_clientCertificateCheckRequested").SetValue(sessionManager, 1);

        // Act
        await sessionManager.CheckClientCertificateIfDueAsync(CancellationToken.None);
        var thumbprintWithinInterval = GetClientCertificateThumbprint(sessionManager);

        timeProvider.Advance(SessionManager.RequestedClientCertificateCheckInterval);
        await sessionManager.CheckClientCertificateIfDueAsync(CancellationToken.None);

        // Assert
        Assert.Equal(originalThumbprint, thumbprintWithinInterval);
        Assert.Equal(swapped.Thumbprint, GetClientCertificateThumbprint(sessionManager));
    }

    [Fact]
    public async Task WhenTheCertificateIsNearExpiry_ThenTheWarningRepeatsAtMostOncePerDay()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        var logger = new RecordingLogger();
        var (source, configuration) = CreateSourceAndConfiguration();
        await using var sessionManager = CreateSessionManager(source, configuration, timeProvider, logger);
        var application = await configuration.CreateApplicationInstanceAsync();
        using var currentCertificate = await LoadCertificateAsync(application, configuration);
        using var expiring = await SwapCertificateInStoreAsync(application, currentCertificate, TimeSpan.FromDays(10));
        AttachApplication(sessionManager, application, expiring, timeProvider);

        // Act
        var warningsPerCheck = new List<int>();
        for (var hour = 0; hour < 26; hour++)
        {
            timeProvider.Advance(SessionManager.ClientCertificateCheckInterval);
            await sessionManager.CheckClientCertificateIfDueAsync(CancellationToken.None);
            warningsPerCheck.Add(CountExpiryWarnings(logger));
        }

        // Assert
        Assert.Equal(1, warningsPerCheck[0]);
        Assert.Equal(1, warningsPerCheck[22]);
        Assert.Equal(2, warningsPerCheck[25]);
    }

    private (OpcUaSubjectClientSource Source, OpcUaClientConfiguration Configuration) CreateSourceAndConfiguration()
    {
        var configuration = CreateConfiguration(certificateStoreBasePath: _storePath);
        configuration.EnablePollingFallback = false;
        configuration.EnableReadAfterWrite = false;
        return (CreateClientSource(configuration: configuration), configuration);
    }

    private static SessionManager CreateSessionManager(
        OpcUaSubjectClientSource source, OpcUaClientConfiguration configuration, TimeProvider timeProvider, ILogger? logger = null) =>
        new(
            source,
            new SubjectPropertyWriter(source, NullLogger.Instance),
            configuration,
            source.PollingMetrics,
            source.ReadAfterWriteMetrics,
            logger ?? NullLogger.Instance,
            timeProvider);

    private static ConcurrentQueue<CertificateChangeEvent> SubscribeToCertificateChanges(ApplicationInstance application)
    {
        var changes = new ConcurrentQueue<CertificateChangeEvent>();
        ((ICertificateLifecycle)application.ApplicationConfiguration!.CertificateManager)
            .CertificateChanges.Subscribe(new Observer(changes.Enqueue));
        return changes;
    }

    private static int CountUpdates(IEnumerable<CertificateChangeEvent> changes) =>
        changes.Count(change => change.Kind == CertificateChangeKind.ApplicationCertificateUpdated);

    private static int CountExpiryWarnings(RecordingLogger logger) =>
        logger.Warnings.Count(warning => warning.Contains("expires", StringComparison.Ordinal));

    /// <summary>
    /// Replaces the certificate in the own store with a new self-signed one for the same application, valid for
    /// <paramref name="lifetime"/>, the way an operator provisions a certificate by hand.
    /// </summary>
    private static async Task<Certificate> SwapCertificateInStoreAsync(
        ApplicationInstance application, Certificate current, TimeSpan lifetime)
    {
        var applicationConfiguration = application.ApplicationConfiguration!;
        var identifier = applicationConfiguration.SecurityConfiguration.ApplicationCertificate!;
        Directory.Delete(identifier.StorePath!, recursive: true);

        var certificate = DefaultCertificateFactory.Instance
            .CreateApplicationCertificate(applicationConfiguration.ApplicationUri!, applicationConfiguration.ApplicationName!, current.Subject)
            .SetNotBefore(DateTime.UtcNow.AddDays(-1))
            .SetLifeTime(lifetime)
            .CreateForRSA();
        using var store = new CertificateStoreIdentifier(identifier.StorePath!, CertificateStoreType.Directory, false)
            .OpenStore(NullTelemetryContext.Instance);
        await store.AddAsync(certificate, password: null);
        return certificate;
    }

    private static Task<Certificate> LoadCertificateAsync(ApplicationInstance application, OpcUaClientConfiguration configuration) =>
        ApplicationCertificateLoader.EnsureLoadedAsync(
            application,
            autoCreate: true,
            configuration.ApplicationCertificateLifetimeInMonths,
            configuration.ResolvedTelemetryContext,
            NullLogger.Instance,
            CancellationToken.None);

    /// <summary>
    /// Sets up the state <see cref="SessionManager.CreateSessionAsync"/> leaves for a secured session, without a server.
    /// </summary>
    private static void AttachApplication(
        SessionManager sessionManager, ApplicationInstance application, Certificate certificate, TimeProvider timeProvider)
    {
        GetField("_application").SetValue(sessionManager, application);
        typeof(SessionManager)
            .GetMethod("ReplaceClientCertificate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sessionManager, [certificate.AddRef()]);
        GetField("_lastClientCertificateCheckTimestamp").SetValue(sessionManager, timeProvider.GetTimestamp());
    }

    private static string? GetClientCertificateThumbprint(SessionManager sessionManager) =>
        (string?)typeof(SessionManager)
            .GetMethod("GetClientCertificateThumbprint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sessionManager, []);

    private static FieldInfo GetField(string name) =>
        typeof(SessionManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private sealed class Observer(Action<CertificateChangeEvent> onNext) : IObserver<CertificateChangeEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(CertificateChangeEvent value) => onNext(value);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp = 1;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
