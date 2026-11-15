using System.Collections.Concurrent;
using System.Reflection;
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
    public async Task WhenTheCertificateInTheStoreIsReplaced_ThenTheNextDueCheckHandsItToTheCertificateManager()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        var (source, configuration) = CreateSourceAndConfiguration();
        await using var sessionManager = CreateSessionManager(source, configuration, timeProvider);
        var application = await configuration.CreateApplicationInstanceAsync();
        using var currentCertificate = await LoadCertificateAsync(application, configuration);
        var originalThumbprint = currentCertificate.Thumbprint;
        AttachApplication(sessionManager, application, currentCertificate, timeProvider);

        var changes = new ConcurrentQueue<CertificateChangeEvent>();
        using var subscription = ((ICertificateLifecycle)application.ApplicationConfiguration!.CertificateManager)
            .CertificateChanges.Subscribe(new Observer(changes.Enqueue));

        Directory.Delete(application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate!.StorePath!, recursive: true);

        // Act
        await sessionManager.CheckClientCertificateIfDueAsync(CancellationToken.None);
        var updatesBeforeDue = changes.Count(change => change.Kind == CertificateChangeKind.ApplicationCertificateUpdated);

        timeProvider.Advance(SessionManager.ClientCertificateCheckInterval);
        await sessionManager.CheckClientCertificateIfDueAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, updatesBeforeDue);
        Assert.NotEqual(originalThumbprint, GetClientCertificateThumbprint(sessionManager));
        await AsyncTestHelpers.WaitUntilAsync(
            () => changes.Any(change => change.Kind == CertificateChangeKind.ApplicationCertificateUpdated),
            timeout: TimeSpan.FromSeconds(10),
            message: "The replacement should be handed to the certificate manager");
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

        var changes = new ConcurrentQueue<CertificateChangeEvent>();
        using var subscription = ((ICertificateLifecycle)application.ApplicationConfiguration!.CertificateManager)
            .CertificateChanges.Subscribe(new Observer(changes.Enqueue));

        // Act
        timeProvider.Advance(SessionManager.ClientCertificateCheckInterval);
        await sessionManager.CheckClientCertificateIfDueAsync(CancellationToken.None);

        // Assert
        Assert.Equal(originalThumbprint, GetClientCertificateThumbprint(sessionManager));
        Assert.DoesNotContain(changes, change => change.Kind == CertificateChangeKind.ApplicationCertificateUpdated);
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
        var originalThumbprint = currentCertificate.Thumbprint;
        AttachApplication(sessionManager, application, currentCertificate, timeProvider);

        var changes = new ConcurrentQueue<CertificateChangeEvent>();
        using var subscription = ((ICertificateLifecycle)application.ApplicationConfiguration!.CertificateManager)
            .CertificateChanges.Subscribe(new Observer(changes.Enqueue));

        Directory.Delete(application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate!.StorePath!, recursive: true);
        GetField("_clientCertificateCheckRequested").SetValue(sessionManager, 1);

        // Act
        await sessionManager.CheckClientCertificateIfDueAsync(CancellationToken.None);

        // Assert
        Assert.NotEqual(originalThumbprint, GetClientCertificateThumbprint(sessionManager));
        await AsyncTestHelpers.WaitUntilAsync(
            () => changes.Any(change => change.Kind == CertificateChangeKind.ApplicationCertificateUpdated),
            timeout: TimeSpan.FromSeconds(10),
            message: "A requested check should hand the replacement to the certificate manager without waiting for the interval");
    }

    private (OpcUaSubjectClientSource Source, OpcUaClientConfiguration Configuration) CreateSourceAndConfiguration()
    {
        var configuration = CreateConfiguration(certificateStoreBasePath: _storePath);
        configuration.EnablePollingFallback = false;
        configuration.EnableReadAfterWrite = false;
        return (CreateClientSource(configuration: configuration), configuration);
    }

    private static SessionManager CreateSessionManager(
        OpcUaSubjectClientSource source, OpcUaClientConfiguration configuration, TimeProvider timeProvider) =>
        new(
            source,
            new SubjectPropertyWriter(source, NullLogger.Instance),
            configuration,
            source.PollingMetrics,
            source.ReadAfterWriteMetrics,
            NullLogger.Instance,
            timeProvider);

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
