using System.Runtime.CompilerServices;
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
/// The session's lifetime token: when it ends as the session stops being current, and that the connector's value reads and
/// writes on such a session end promptly through it.
/// </summary>
public class SessionLifetimeTests
{
    [Fact]
    public async Task WhenTheSessionLifetimeEndsWithAWriteInFlight_ThenTheWriteFailsPromptly()
    {
        // Arrange
        var configuration = CreateConfigurationWithoutBackgroundReads();
        await using var source = CreateClientSource(configuration: configuration);
        await using var sessionManager = CreateSessionManager(source, configuration);

        var channel = new OrphaningTransportChannel(configuration.ResolvedTelemetryContext);
        using var session = CreateConnectedSession(channel, configuration.ResolvedTelemetryContext);
        using var sessionLifetime = new CancellationTokenSource();

        var writer = new OutboundWriter(
            () => (session, sessionLifetime.Token),
            readAfterWriteManager: null,
            sessionManager.ScheduleFullStateSyncAfterResponseMismatch,
            configuration,
            source.OpcUaNodeIdKey,
            new ThroughputCounter(),
            NullLogger.Instance);

        var changes = CreateChanges(source.OpcUaNodeIdKey);
        var write = writer.WriteChangesAsync(changes, CancellationToken.None).AsTask();
        await channel.RequestSent.WaitAsync(TestTimeouts.Default);

        // Act
        await sessionLifetime.CancelAsync();

        // Assert
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
        var firstSession = CreateUnconnectedManagedSession();
        var secondSession = CreateUnconnectedManagedSession();

        try
        {
            sessionManager.SetSession(firstSession);
            var (_, firstLifetime) = sessionManager.CurrentSessionWithLifetime;

            // Act
            sessionManager.SetSession(secondSession);
            var (currentSession, secondLifetime) = sessionManager.CurrentSessionWithLifetime;

            // Assert
            Assert.True(firstLifetime.IsCancellationRequested, "The replaced session's lifetime was not canceled.");
            Assert.Same(secondSession, currentSession);
            Assert.True(secondLifetime.CanBeCanceled);
            Assert.False(secondLifetime.IsCancellationRequested, "The current session's lifetime was canceled.");
        }
        finally
        {
            sessionManager.SetSession(null);
        }
    }

    [Fact]
    public async Task WhenTheSameSessionIsSetAgain_ThenItsLifetimeIsKept()
    {
        // Arrange
        var configuration = CreateConfigurationWithoutBackgroundReads();
        await using var source = CreateClientSource(configuration: configuration);
        await using var sessionManager = CreateSessionManager(source, configuration);
        var session = CreateUnconnectedManagedSession();

        try
        {
            sessionManager.SetSession(session);
            var (_, lifetime) = sessionManager.CurrentSessionWithLifetime;

            // Act
            sessionManager.SetSession(session);

            // Assert
            Assert.False(lifetime.IsCancellationRequested, "Setting the current session again canceled its lifetime.");
            Assert.Equal(lifetime, sessionManager.CurrentSessionWithLifetime.Lifetime);
        }
        finally
        {
            sessionManager.SetSession(null);
        }
    }

    [Fact]
    public async Task WhenTheSessionIsCleared_ThenItsLifetimeIsCanceledAndNoSessionIsReported()
    {
        // Arrange
        var configuration = CreateConfigurationWithoutBackgroundReads();
        await using var source = CreateClientSource(configuration: configuration);
        await using var sessionManager = CreateSessionManager(source, configuration);
        var session = CreateUnconnectedManagedSession();
        sessionManager.SetSession(session);
        var (_, lifetime) = sessionManager.CurrentSessionWithLifetime;

        // Act
        sessionManager.SetSession(null);

        // Assert
        Assert.True(lifetime.IsCancellationRequested, "The cleared session's lifetime was not canceled.");
        var (currentSession, currentLifetime) = sessionManager.CurrentSessionWithLifetime;
        Assert.Null(currentSession);
        Assert.False(currentLifetime.CanBeCanceled);
    }

    [Fact]
    public async Task WhenTheSessionManagerIsDisposed_ThenTheSessionIsClearedWithItsLifetimeEnded()
    {
        // Arrange
        var configuration = CreateConfigurationWithoutBackgroundReads();
        await using var source = CreateClientSource(configuration: configuration);
        var sessionManager = CreateSessionManager(source, configuration);

        sessionManager.SetSession(CreateUnconnectedManagedSession());
        sessionManager.DrainPendingSessionTransitions();
        var lifetime = sessionManager.CurrentSessionWithLifetime.Lifetime;

        (bool HasCurrentSession, bool LifetimeEnded)? stateAtChangeToNull = null;
        source.CurrentSessionChanged += (_, eventArgs) =>
        {
            if (eventArgs.CurrentSession is null)
            {
                stateAtChangeToNull ??= (sessionManager.CurrentSession is not null, lifetime.IsCancellationRequested);
            }
        };

        // Act
        await sessionManager.DisposeAsync();

        // Assert
        Assert.Equal((HasCurrentSession: false, LifetimeEnded: true), stateAtChangeToNull);
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

    /// <summary>
    /// A managed session that was never connected: enough to be set as the current session and to carry a
    /// lifetime, and its close and dispose fail without side effects.
    /// </summary>
    private static ManagedSession CreateUnconnectedManagedSession() =>
        (ManagedSession)RuntimeHelpers.GetUninitializedObject(typeof(ManagedSession));

    private static Session CreateConnectedSession(ITransportChannel channel, ITelemetryContext telemetry)
    {
        var applicationConfiguration = new ApplicationConfiguration(telemetry)
        {
            ApplicationName = "Test",
            ApplicationType = ApplicationType.Client,
            ClientConfiguration = new ClientConfiguration(),
            SecurityConfiguration = new SecurityConfiguration()
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
    /// A channel that never answers a request in flight and drops it on Dispose without completing it, so only
    /// the request's token ends it, with BadRequestInterrupted as the SDK reports a canceled request. The operation timeout is left out, so a request
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

        public byte[] ChannelThumbprint => [];

        public byte[] ClientChannelCertificate => [];

        public byte[] ServerChannelCertificate => [];

        public IServiceMessageContext MessageContext { get; } = ServiceMessageContext.Create(telemetry);

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
