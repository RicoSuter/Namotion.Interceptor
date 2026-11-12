using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Client.Subscriptions;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Client.Connection;

/// <summary>
/// Owns the OPC UA session. Reconnection, session recreation and subscription restoration are driven
/// by the SDK's <see cref="ManagedSession"/>; this class maps its state transitions onto the source's
/// liveness, reconnection metrics and full state synchronization.
/// </summary>
internal sealed class SessionManager : IAsyncDisposable, IDisposable
{
    private readonly OpcUaSubjectClientSource _source;
    private readonly SubjectPropertyWriter _propertyWriter;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;

    private ManagedSession? _session;

    // Serializes liveness reports with the state-change callback, so a stale read in
    // ReportLivenessFromSessionState cannot raise liveness after a transition dropped it.
    private readonly Lock _stateLock = new();

    // Transitions are enqueued by SetSession and drained outside any lock, so user-supplied
    // CurrentSessionChanged handlers never run with a lock held.
    private readonly ConcurrentQueue<(ISession? Previous, ISession? Current)> _pendingSessionTransitions = new();
    private readonly Lock _drainLock = new();

    // The managed session's own Connected only says that a server session id exists, which stays true
    // through an outage, so the state is tracked from its transitions.
    private int _connectionState = (int)ConnectionState.Disconnected;
    private ServiceResult? _lastReconnectError; // guarded by _stateLock
    private int _reconnectInProgress; // 1 = a reconnect cycle has not resolved yet
    private int _needsFullStateSync;
    private int _disposed;

    // Owns the certificate manager and its timer, so it is disposed with this manager.
    private ApplicationInstance? _application;

    /// <summary>
    /// Gets the current session. The managed session stays the same object across reconnects and
    /// session recreation; it is only replaced when the source restarts.
    /// </summary>
    public ManagedSession? CurrentSession => Volatile.Read(ref _session);

    /// <summary>
    /// Gets a value indicating whether the session is connected and not reconnecting.
    /// </summary>
    public bool IsConnected =>
        Volatile.Read(ref _session) is { Connected: true } &&
        (ConnectionState)Volatile.Read(ref _connectionState) is ConnectionState.Connected;

    /// <summary>
    /// Gets a value indicating whether the managed session is recovering the connection, including
    /// the pause between two reconnection cycles.
    /// </summary>
    public bool IsReconnecting =>
        Volatile.Read(ref _session) is not null &&
        (ConnectionState)Volatile.Read(ref _connectionState)
            is ConnectionState.Reconnecting or ConnectionState.Failover or ConnectionState.Disconnected;

    /// <summary>
    /// Gets a value indicating whether this manager has been disposed.
    /// </summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <summary>
    /// Gets the current subscriptions managed by the subscription manager.
    /// </summary>
    public IReadOnlyCollection<ISubscription> Subscriptions => SubscriptionManager.Subscriptions;

    internal SubscriptionManager SubscriptionManager { get; }

    internal PollingManager? PollingManager { get; }

    internal ReadAfterWriteManager? ReadAfterWriteManager { get; }

    internal PollingDiagnostics? PollingDiagnostics { get; }

    internal ReadAfterWriteDiagnostics? ReadAfterWriteDiagnostics { get; }

    public SessionManager(
        OpcUaSubjectClientSource source,
        SubjectPropertyWriter propertyWriter,
        OpcUaClientConfiguration configuration,
        PollingMetrics pollingMetrics,
        ReadAfterWriteMetrics readAfterWriteMetrics,
        ILogger logger)
    {
        _source = source;
        _propertyWriter = propertyWriter;
        _logger = logger;
        _configuration = configuration;

        if (_configuration.EnablePollingFallback)
        {
            PollingManager = new PollingManager(
                source,
                sessionProvider: () => CurrentSession,
                propertyWriter,
                _configuration,
                pollingMetrics,
                source.ReportBackgroundError,
                _logger);

            PollingDiagnostics = new PollingDiagnostics(PollingManager, pollingMetrics);

            PollingManager.Start();
        }

        if (_configuration.EnableReadAfterWrite)
        {
            ReadAfterWriteManager = new ReadAfterWriteManager(
                sessionProvider: () => CurrentSession,
                source,
                configuration,
                readAfterWriteMetrics,
                source.ReportBackgroundError,
                logger);

            ReadAfterWriteDiagnostics = new ReadAfterWriteDiagnostics(ReadAfterWriteManager, readAfterWriteMetrics);
        }

        SubscriptionManager = new SubscriptionManager(
            source,
            propertyWriter,
            PollingManager,
            ReadAfterWriteManager,
            configuration,
            source.ReportBackgroundError,
            logger);
    }

    /// <summary>
    /// Performs a full state synchronization after the managed session recovered the connection.
    /// Buffers incoming notifications, reads all property values from the server, replays the buffer
    /// and resumes. On failure the request is restored, so the next health check tick retries.
    /// </summary>
    internal async Task PerformFullStateSyncIfNeededAsync(CancellationToken cancellationToken)
    {
        // Taken before the load rather than cleared after it: a recovery that happens while the load
        // runs requests another sync, and the load it supersedes returns without applying anything.
        if (Interlocked.Exchange(ref _needsFullStateSync, 0) != 1)
        {
            return;
        }

        _logger.LogInformation("Performing full state sync after OPC UA session recreation...");
        _propertyWriter.StartBuffering();
        try
        {
            await _propertyWriter.LoadInitialStateAndResumeAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Full state sync completed successfully.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _needsFullStateSync, 1);
            _source.ReportBackgroundError(ex, cancellationToken);
            _logger.LogError(ex, "Full state sync failed. Retrying on the next health check.");
        }
    }

    /// <summary>
    /// Reports the source's liveness from the current session state: raised when the session is
    /// connected and not reconnecting, dropped otherwise.
    /// </summary>
    internal void ReportLivenessFromSessionState()
    {
        lock (_stateLock)
        {
            if (IsConnected)
            {
                _source.NotifySessionHealthy();
            }
            else
            {
                _source.NotifySessionNotHealthy();
            }
        }
    }

    /// <summary>
    /// Creates the managed session. The managed session then owns reconnection for its lifetime.
    /// </summary>
    public async Task<ManagedSession> CreateSessionAsync(
        ApplicationInstance application,
        OpcUaClientConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var applicationConfiguration = application.ApplicationConfiguration
            ?? throw new InvalidOperationException("The OPC UA application instance has no application configuration.");
        Volatile.Write(ref _application, application);
        var endpointConfiguration = EndpointConfiguration.Create(applicationConfiguration);
        var serverUri = new Uri(configuration.ServerUrl);

        ArrayOf<EndpointDescription> endpoints;
        try
        {
            using var discoveryClient = await DiscoveryClient.CreateAsync(
                applicationConfiguration,
                serverUri,
                endpointConfiguration, ct: cancellationToken).ConfigureAwait(false);

            endpoints = await discoveryClient.GetEndpointsAsync(default, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to discover OPC UA endpoints at '{configuration.ServerUrl}'. " +
                $"Verify the server is running and the URL is correct. " +
                $"The connection will be retried automatically.",
                ex);
        }

        var endpointDescription = CoreClientUtils.SelectEndpoint(
            applicationConfiguration,
            serverUri,
            endpoints,
            useSecurity: configuration.UseSecurity,
            configuration.ResolvedTelemetryContext)
            ?? throw new InvalidOperationException(
                $"No suitable OPC UA endpoint found at '{configuration.ServerUrl}' (UseSecurity: {configuration.UseSecurity}).");

        var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

        // One policy cycle is one reconnect attempt in the diagnostics: when its budget is exhausted the
        // managed session reports Disconnected and starts the next cycle by itself.
        var reconnectPolicy = new ReconnectPolicy
        {
            Strategy = BackoffStrategy.Constant,
            InitialDelay = configuration.ReconnectInterval,
            MaxDelay = configuration.ReconnectInterval,
            MaxRetries = 0,
            MaxTotalReconnectTime = configuration.MaxReconnectDuration
        };

        var session = await ManagedSession.CreateAsync(
            applicationConfiguration,
            endpoint,
            configuration.ActualSessionFactory,
            identity: configuration.CreateUserIdentity != null
                ? await configuration.CreateUserIdentity(cancellationToken).ConfigureAwait(false)
                : new UserIdentity(),
            reconnectPolicy: reconnectPolicy,
            telemetry: configuration.ResolvedTelemetryContext,
            sessionName: application.ApplicationName ?? configuration.ApplicationName,
            sessionTimeout: (uint)configuration.SessionTimeout.TotalMilliseconds,
            transferSubscriptionsOnRecreate: true,
            ct: cancellationToken).ConfigureAwait(false);

        session.MinPublishRequestCount = _configuration.MinPublishRequestCount;
        session.KeepAliveInterval = (int)_configuration.KeepAliveInterval.TotalMilliseconds;
        session.ConnectionStateChanged += OnConnectionStateChanged;
        session.SessionGenerationChanged += OnSessionGenerationChanged;

        Volatile.Write(ref _connectionState, (int)ConnectionState.Connected);
        var previousSession = Interlocked.Exchange(ref _session, session);
        _pendingSessionTransitions.Enqueue((previousSession, session));
        DrainPendingSessionTransitions();

        if (previousSession is not null)
        {
            await DisposeSessionAsync(previousSession, cancellationToken).ConfigureAwait(false);
        }

        return session;
    }

    public async Task CreateSubscriptionsAsync(
        IReadOnlyList<MonitoredItemRequest> monitoredItems,
        ISession session, CancellationToken cancellationToken)
    {
        await SubscriptionManager.CreateBatchedSubscriptionsAsync(monitoredItems, session, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Created {SubscriptionCount} subscriptions with {Subscribed} " +
            "total monitored items ({Polled} via polling).",
            SubscriptionManager.SubscriptionCount,
            SubscriptionManager.MonitoredItemCount,
            PollingManager?.PollingItemCount ?? 0);
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        _logger.LogInformation(
            "OPC UA connection state changed from {PreviousState} to {NewState} (attempt {Attempt}).",
            e.PreviousState, e.NewState, e.ReconnectAttempt);

        lock (_stateLock)
        {
            // Checked under the lock DisposeAsync settles the open attempt under, so no attempt can
            // start after that.
            if (Volatile.Read(ref _disposed) == 1 || !ReferenceEquals(sender, Volatile.Read(ref _session)))
            {
                return;
            }

            Volatile.Write(ref _connectionState, (int)e.NewState);

            switch (e.NewState)
            {
                case ConnectionState.Reconnecting or ConnectionState.Failover:
                    if (e.Error is not null && ServiceResult.IsBad(e.Error))
                    {
                        _lastReconnectError = e.Error;
                    }

                    if (Interlocked.Exchange(ref _reconnectInProgress, 1) == 0)
                    {
                        _source.NotifyConnectionLost();
                        _source.ReconnectionMetrics.RecordAttemptStart();
                    }
                    else
                    {
                        _source.NotifySessionNotHealthy();
                    }
                    break;

                case ConnectionState.Disconnected:
                    // The reconnect policy's budget is exhausted. The managed session starts a new
                    // cycle by itself, which is counted as the next attempt.
                    if (Interlocked.Exchange(ref _reconnectInProgress, 0) == 1)
                    {
                        _source.ReconnectionMetrics.RecordFailure();

                        // The error of the last failed connect attempt of the cycle: the one this
                        // transition carries is the result of the failover that follows it.
                        var error = _lastReconnectError ?? e.Error;
                        if (error is not null)
                        {
                            _source.ReportBackgroundError(new ServiceResultException(error));
                        }
                    }

                    _lastReconnectError = null;
                    _source.NotifySessionNotHealthy();
                    break;

                case ConnectionState.Connected:
                    _lastReconnectError = null;
                    if (Interlocked.Exchange(ref _reconnectInProgress, 0) == 1)
                    {
                        _source.ReconnectionMetrics.RecordSuccess();
                    }

                    // Notifications can be lost during an outage even when the server session and
                    // its subscriptions survived, so every recovery ends with a full read.
                    Interlocked.Exchange(ref _needsFullStateSync, 1);

                    if (IsConnected)
                    {
                        _source.NotifySessionHealthy();
                    }
                    break;

                default:
                    _source.NotifySessionNotHealthy();
                    break;
            }
        }
    }

    private void OnSessionGenerationChanged(object? sender, SessionGenerationChangedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 1 || sender is not ManagedSession session ||
            !ReferenceEquals(session, Volatile.Read(ref _session)))
        {
            return;
        }

        if (e.PreviousSessionId.IsNull || e.PreviousSessionId == e.SessionId)
        {
            return;
        }

        _logger.LogInformation(
            "OPC UA server session recreated ({PreviousSessionId} -> {SessionId}). Full state sync pending.",
            e.PreviousSessionId, e.SessionId);

        // The subscription engine moves the subscriptions to the new server session by itself. A
        // transferred one keeps its message numbers, but one created again after a failed transfer
        // numbers them from the start again, hence the new ranking. Notifications may have been
        // missed while no server session existed, hence the full read.
        SubscriptionManager.ResetNotificationRanking();
        ReadAfterWriteManager?.ClearPendingReads();
        Interlocked.Exchange(ref _needsFullStateSync, 1);
    }

    /// <summary>
    /// Closes the transport channel gracefully. The managed session detects the loss and reconnects.
    /// </summary>
    internal async Task DisconnectTransportAsync(CancellationToken cancellationToken)
    {
        var channel = Volatile.Read(ref _session)?.NullableTransportChannel;
        if (channel is null)
        {
            return;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await channel.CloseAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error closing transport channel, falling back to dispose.");
            KillTransport();
        }
    }

    /// <summary>
    /// Disposes the transport channel abruptly, so in-flight operations fail. The managed session
    /// detects the loss and reconnects.
    /// </summary>
    internal void KillTransport()
    {
        try
        {
            Volatile.Read(ref _session)?.NullableTransportChannel?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error killing transport channel.");
        }
    }

    private async Task DisposeSessionAsync(ManagedSession session, CancellationToken cancellationToken)
    {
        session.ConnectionStateChanged -= OnConnectionStateChanged;
        session.SessionGenerationChanged -= OnSessionGenerationChanged;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_configuration.SessionDisposalTimeout);
            await session.CloseAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error closing OPC UA session (id={SessionId}).", session.SessionId);
        }

        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing OPC UA session.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        lock (_stateLock)
        {
            if (Interlocked.Exchange(ref _reconnectInProgress, 0) == 1)
            {
                _source.ReconnectionMetrics.RecordAbandoned();
            }
        }

        if (ReadAfterWriteManager is not null)
        {
            try { await ReadAfterWriteManager.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing read-after-write manager."); }
        }
        try { await SubscriptionManager.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing subscription manager."); }
        if (PollingManager is not null)
        {
            try { await PollingManager.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing polling manager."); }
        }

        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            using var cts = new CancellationTokenSource(_configuration.SessionDisposalTimeout);
            await DisposeSessionAsync(session, cts.Token).ConfigureAwait(false);
            _pendingSessionTransitions.Enqueue((session, null));
        }

        DrainPendingSessionTransitions();

        var application = Interlocked.Exchange(ref _application, null);
        if (application is not null)
        {
            try { await application.DisposeAsync().AsTask().WaitAsync(_configuration.SessionDisposalTimeout).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error disposing OPC UA application instance."); }
        }
    }

    /// <summary>
    /// Fires <see cref="OpcUaSubjectClientSource.OnCurrentSessionChanged"/> for every pending session
    /// transition. Concurrent drainers serialize on <see cref="_drainLock"/>, preserving order.
    /// </summary>
    internal void DrainPendingSessionTransitions()
    {
        lock (_drainLock)
        {
            while (_pendingSessionTransitions.TryDequeue(out var transition))
            {
                _source.OnCurrentSessionChanged(transition.Previous, transition.Current);
            }
        }
    }

    /// <summary>
    /// Satisfies IDisposable for interface compatibility by delegating to <see cref="DisposeAsync"/>.
    /// </summary>
    void IDisposable.Dispose()
    {
        _logger.LogWarning("Sync Dispose() called on SessionManager - prefer DisposeAsync() for proper cleanup.");
        _ = Task.Run(async () =>
        {
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during fire-and-forget disposal of SessionManager.");
            }
        });
    }
}
