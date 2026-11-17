using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;
using static Namotion.Interceptor.OpcUa.Tests.Client.ClientSourceTestFactory;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// A request in flight on a session the client stops using must end promptly. The SDK does not guarantee
/// that: disposing a transport channel drops its pending requests without completing them, so such a request
/// ends only at its operation timeout or when its token is canceled. Each session therefore carries a lifetime
/// token that is canceled once the session stops being current.
/// </summary>
public class SessionLifetimeTests
{
    [Fact]
    public async Task WhenAPreservedSessionIsAbandonedWithAWriteInFlight_ThenTheWriteFailsPromptly()
    {
        // Arrange
        var configuration = CreateConfigurationWithoutBackgroundReads();
        await using var source = CreateClientSource(configuration: configuration);
        await using var sessionManager = CreateSessionManager(source, configuration);

        var channel = new OrphaningTransportChannel(configuration.ResolvedTelemetryContext);
        var session = CreateConnectedSession(channel, configuration.ResolvedTelemetryContext);
        SetSession(sessionManager, session);

        var writer = new OutboundWriter(
            () => sessionManager.CurrentSessionWithLifetime,
            readAfterWriteManager: null,
            sessionManager.ScheduleFullStateSyncAfterResponseMismatch,
            configuration,
            source.OpcUaNodeIdKey,
            new ThroughputCounter(),
            NullLogger.Instance);

        var changes = CreateChanges(source.OpcUaNodeIdKey);
        var write = writer.WriteChangesAsync(changes, CancellationToken.None).AsTask();
        await channel.RequestSent.WaitAsync(TestTimeouts.Default);

        // Act: the SDK reconnect handler reports the same session object ("preserved"), which the client abandons.
        var reconnectHandler = (SessionReconnectHandler)typeof(SessionManager)
            .GetField("_reconnectHandler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sessionManager)!;
        typeof(SessionReconnectHandler).GetProperty(nameof(SessionReconnectHandler.Session))!.SetValue(reconnectHandler, session);
        typeof(SessionManager)
            .GetMethod("OnReconnectComplete", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sessionManager, [reconnectHandler, EventArgs.Empty]);

        // Assert
        Assert.Null(sessionManager.CurrentSession);
        Assert.True(channel.IsDisposed, "The abandon did not kill the transport channel.");

        var result = await write.WaitAsync(TestTimeouts.Default);
        Assert.NotNull(result.Error);
        Assert.Equal(changes.Length, result.FailedChanges.Length);
    }

    [Fact]
    public async Task WhenTheSessionLifetimeEndsWithAReadInFlight_ThenTheReadFailsPromptly()
    {
        // Arrange
        var telemetry = CreateConfiguration().ResolvedTelemetryContext;
        var channel = new OrphaningTransportChannel(telemetry);
        using var session = CreateConnectedSession(channel, telemetry);
        using var sessionLifetime = new CancellationTokenSource();

        var read = OpcUaValueReader.ReadAsync(
            session,
            sessionLifetime.Token,
            [new ReadValueId { NodeId = new NodeId("FirstName", 2), AttributeId = Opc.Ua.Attributes.Value }],
            TimestampsToReturn.Source,
            CancellationToken.None).AsTask();
        await channel.RequestSent.WaitAsync(TestTimeouts.Default);

        // Act
        await sessionLifetime.CancelAsync();

        // Assert
        var exception = await Assert.ThrowsAsync<ServiceResultException>(() => read.WaitAsync(TestTimeouts.Default));
        Assert.Equal(StatusCodes.BadRequestInterrupted, exception.StatusCode);
    }

    [Fact]
    public async Task WhenTheSessionIsReplaced_ThenOnlyThePreviousLifetimeIsCanceled()
    {
        // Arrange
        var configuration = CreateConfigurationWithoutBackgroundReads();
        await using var source = CreateClientSource(configuration: configuration);
        await using var sessionManager = CreateSessionManager(source, configuration);
        var telemetry = configuration.ResolvedTelemetryContext;
        using var firstSession = CreateConnectedSession(new OrphaningTransportChannel(telemetry), telemetry);
        using var secondSession = CreateConnectedSession(new OrphaningTransportChannel(telemetry), telemetry);

        try
        {
            SetSession(sessionManager, firstSession);
            var (_, firstLifetime) = sessionManager.CurrentSessionWithLifetime;

            // Act
            SetSession(sessionManager, secondSession);
            var (currentSession, secondLifetime) = sessionManager.CurrentSessionWithLifetime;

            // Assert
            Assert.True(firstLifetime.IsCancellationRequested, "The replaced session's lifetime was not canceled.");
            Assert.Same(secondSession, currentSession);
            Assert.True(secondLifetime.CanBeCanceled);
            Assert.False(secondLifetime.IsCancellationRequested, "The current session's lifetime was canceled.");
        }
        finally
        {
            SetSession(sessionManager, null);
        }
    }

    [Fact]
    public async Task WhenTheSameSessionIsSetAgain_ThenItsLifetimeIsKept()
    {
        // Arrange
        var configuration = CreateConfigurationWithoutBackgroundReads();
        await using var source = CreateClientSource(configuration: configuration);
        await using var sessionManager = CreateSessionManager(source, configuration);
        var telemetry = configuration.ResolvedTelemetryContext;
        using var session = CreateConnectedSession(new OrphaningTransportChannel(telemetry), telemetry);

        try
        {
            SetSession(sessionManager, session);
            var (_, lifetime) = sessionManager.CurrentSessionWithLifetime;

            // Act
            SetSession(sessionManager, session);

            // Assert
            Assert.False(lifetime.IsCancellationRequested, "Setting the current session again canceled its lifetime.");
            Assert.Equal(lifetime, sessionManager.CurrentSessionWithLifetime.Lifetime);
        }
        finally
        {
            SetSession(sessionManager, null);
        }
    }

    [Fact]
    public async Task WhenTheSessionIsCleared_ThenItsLifetimeIsCanceledAndNoSessionIsReported()
    {
        // Arrange
        var configuration = CreateConfigurationWithoutBackgroundReads();
        await using var source = CreateClientSource(configuration: configuration);
        await using var sessionManager = CreateSessionManager(source, configuration);
        var telemetry = configuration.ResolvedTelemetryContext;
        using var session = CreateConnectedSession(new OrphaningTransportChannel(telemetry), telemetry);
        SetSession(sessionManager, session);
        var (_, lifetime) = sessionManager.CurrentSessionWithLifetime;

        // Act
        SetSession(sessionManager, null);

        // Assert
        Assert.True(lifetime.IsCancellationRequested, "The cleared session's lifetime was not canceled.");
        var (currentSession, currentLifetime) = sessionManager.CurrentSessionWithLifetime;
        Assert.Null(currentSession);
        Assert.False(currentLifetime.CanBeCanceled);
    }

    private static OpcUaClientConfiguration CreateConfigurationWithoutBackgroundReads()
    {
        var configuration = CreateConfiguration();
        configuration.EnablePollingFallback = false;
        configuration.EnableReadAfterWrite = false;
        return configuration;
    }

    private static SessionManager CreateSessionManager(OpcUaSubjectClientSource source, OpcUaClientConfiguration configuration) =>
        new(
            source,
            new SubjectPropertyWriter(source, NullLogger.Instance),
            configuration,
            source.PollingMetrics,
            source.ReadAfterWriteMetrics,
            NullLogger.Instance);

    private static void SetSession(SessionManager sessionManager, Session? session) =>
        typeof(SessionManager)
            .GetMethod("SetSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sessionManager, [session]);

    private static Session CreateConnectedSession(ITransportChannel channel, ITelemetryContext telemetry)
    {
        var applicationConfiguration = new ApplicationConfiguration(telemetry)
        {
            ApplicationName = "Test",
            ApplicationType = ApplicationType.Client,
            ClientConfiguration = new ClientConfiguration(),
            SecurityConfiguration = new SecurityConfiguration(),
            CertificateValidator = new CertificateValidator(telemetry)
        };

        var endpoint = new ConfiguredEndpoint(
            null, new EndpointDescription("opc.tcp://localhost:4840"), EndpointConfiguration.Create());

        var session = new Session(channel, applicationConfiguration, endpoint);
        session.SessionCreated(new NodeId(1u), new NodeId(2u)); // Connected reports a session ID.
        return session;
    }

    private static SubjectPropertyChange[] CreateChanges(string nodeIdKey)
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var subject = new TestPerson(context);
        var property = new PropertyReference(subject, nameof(TestPerson.FirstName));
        property.SetPropertyData(nodeIdKey, new NodeId("FirstName", 2));
        return [SubjectPropertyChange.Create(property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, string.Empty, "value")];
    }

    /// <summary>
    /// Mirrors the SDK 1.5.378 binary channel for a request in flight: it never answers, Dispose drops the
    /// request without completing it, and only the request's token ends it, with BadRequestInterrupted as
    /// ChannelAsyncOperation.EndAsync reports a cancellation. The operation timeout is left out, so a request
    /// that only the timeout would end hangs past the test's bound.
    /// </summary>
    private sealed class OrphaningTransportChannel(ITelemetryContext telemetry) : ITransportChannel
    {
        private readonly TaskCompletionSource _requestSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _isDisposed;

        public Task RequestSent => _requestSent.Task;

        public bool IsDisposed => _isDisposed;

        public TransportChannelFeatures SupportedFeatures => TransportChannelFeatures.None;

        public EndpointDescription EndpointDescription { get; } = new("opc.tcp://localhost:4840");

        public EndpointConfiguration EndpointConfiguration { get; } = EndpointConfiguration.Create();

        public IServiceMessageContext MessageContext { get; } = new ServiceMessageContext(telemetry);

        public int OperationTimeout { get; set; } = 30_000;

        public async ValueTask<IServiceResponse> SendRequestAsync(IServiceRequest request, CancellationToken ct = default)
        {
            if (_isDisposed)
            {
                throw new ServiceResultException(StatusCodes.BadNotConnected);
            }

            _requestSent.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new ServiceResultException(StatusCodes.BadRequestInterrupted);
            }

            throw new InvalidOperationException("Unreachable.");
        }

        public ValueTask ReconnectAsync(ITransportWaitingConnection? connection = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask CloseAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public void Dispose() => _isDisposed = true;
    }
}
