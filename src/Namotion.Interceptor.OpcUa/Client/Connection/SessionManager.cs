using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Client.Connection;

/// <summary>
/// Manages OPC UA session lifecycle, reconnection handling, and thread-safe session access.
/// Optimized for fast session reads (hot path) with simple lock-based writes (cold path).
/// </summary>
internal sealed class SessionManager : IAsyncDisposable, IDisposable
{
    private readonly OpcUaSubjectClientSource _source;
    private readonly SubjectPropertyWriter _propertyWriter;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;

    private volatile SessionReconnectHandler _reconnectHandler;

    private Session? _session;

    private readonly object _reconnectingLock = new();

    private int _isReconnecting; // 0 = false, 1 = true (thread-safe via Interlocked)
    private int _disposed; // 0 = false, 1 = true (thread-safe via Interlocked)

    // Fields for deferred async work (handled by health check loop)
    // Enqueued by SDK reconnection callbacks (sync), drained by health check loop via DisposePendingSessionsAsync (async).
    private readonly ConcurrentQueue<Session> _sessionsToDispose = new();
    private int _needsInitialization; // 0 = false, 1 = true (thread-safe via Interlocked)
    private int _needsBufferResume;   // 0 = false, 1 = true (thread-safe via Interlocked)

    /// <summary>
    /// Gets the current session. WARNING: Can change at any time due to reconnection. Never cache - read immediately before use.
    /// </summary>
    public Session? CurrentSession => Volatile.Read(ref _session);

    /// <summary>
    /// Gets a value indicating whether the session is currently connected and not reconnecting.
    /// Returns true only if there is a session, it reports as connected, AND we're not in a reconnection cycle.
    /// </summary>
    public bool IsConnected =>
        Volatile.Read(ref _isReconnecting) == 0 &&
        (Volatile.Read(ref _session)?.Connected ?? false);

    /// <summary>
    /// Gets a value indicating whether the session is currently reconnecting.
    /// </summary>
    public bool IsReconnecting => Volatile.Read(ref _isReconnecting) == 1;

    /// <summary>
    /// Gets a value indicating whether initialization needs to be completed by the health check loop.
    /// Set when SDK reconnection succeeds (subscription transfer or preserved session).
    /// </summary>
    public bool NeedsInitialization => Volatile.Read(ref _needsInitialization) == 1;

    /// <summary>
    /// Gets a value indicating whether the buffer should be replayed after a preserved session reconnect.
    /// Unlike full initialization, this does not require a server round-trip.
    /// </summary>
    public bool NeedsBufferResume => Volatile.Read(ref _needsBufferResume) == 1;

    /// <summary>
    /// Gets whether there are sessions waiting for async disposal by the health check loop.
    /// </summary>
    public bool HasSessionsToDispose => !_sessionsToDispose.IsEmpty;

    /// <summary>
    /// Gets the current subscriptions managed by the subscription manager.
    /// </summary>
    public IReadOnlyCollection<Subscription> Subscriptions => SubscriptionManager.Subscriptions;

    /// <summary>
    /// Gets the subscription manager for cleanup operations.
    /// </summary>
    internal SubscriptionManager SubscriptionManager { get; }

    /// <summary>
    /// Gets the polling manager for cleanup operations (may be null if polling disabled).
    /// </summary>
    internal PollingManager? PollingManager { get; }

    /// <summary>
    /// Gets the read-after-write manager for scheduling reads after writes.
    /// </summary>
    internal ReadAfterWriteManager? ReadAfterWriteManager { get; private set; }

    public SessionManager(OpcUaSubjectClientSource source, SubjectPropertyWriter propertyWriter, OpcUaClientConfiguration configuration, ILogger logger)
    {
        _source = source;
        _propertyWriter = propertyWriter;
        _logger = logger;
        _configuration = configuration;
        _reconnectHandler = new SessionReconnectHandler(configuration.TelemetryContext, false, (int)configuration.ReconnectHandlerTimeout.TotalMilliseconds);

        if (_configuration.EnablePollingFallback)
        {
            PollingManager = new PollingManager(
                source, sessionManager: this,
                propertyWriter, _configuration, _logger);

            PollingManager.Start();
        }

        if (_configuration.EnableReadAfterWrite)
        {
            ReadAfterWriteManager = new ReadAfterWriteManager(
                sessionProvider: () => CurrentSession,
                source,
                configuration,
                logger);
        }

        SubscriptionManager = new SubscriptionManager(source, propertyWriter, PollingManager, ReadAfterWriteManager, configuration, logger);
    }

    /// <summary>
    /// Create a new OPC UA session with the specified configuration.
    /// Thread-safety: Cannot race with OnReconnectComplete because callers coordinate via IsReconnecting flag.
    /// Manual reconnection (ReconnectSessionAsync) only proceeds when !IsReconnecting, which blocks when
    /// automatic reconnection (OnReconnectComplete) is active. Therefore, no lock needed here.
    /// </summary>
    public async Task<Session> CreateSessionAsync(
        ApplicationInstance application,
        OpcUaClientConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var endpointConfiguration = EndpointConfiguration.Create(application.ApplicationConfiguration);
        var serverUri = new Uri(configuration.ServerUrl);

        EndpointDescriptionCollection endpoints;
        try
        {
            using var discoveryClient = await DiscoveryClient.CreateAsync(
                application.ApplicationConfiguration,
                serverUri,
                endpointConfiguration, ct: cancellationToken).ConfigureAwait(false);

            endpoints = await discoveryClient.GetEndpointsAsync(null, cancellationToken).ConfigureAwait(false);
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
            application.ApplicationConfiguration,
            serverUri,
            endpoints,
            useSecurity: configuration.UseSecurity,
            configuration.TelemetryContext);

        var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);
        var oldSession = Volatile.Read(ref _session);

        var sessionFactory = configuration.ActualSessionFactory;
        var sessionResult = await sessionFactory.CreateAsync(
            application.ApplicationConfiguration,
            endpoint,
            updateBeforeConnect: false,
            sessionName: application.ApplicationName,
            sessionTimeout: (uint)configuration.SessionTimeout.TotalMilliseconds,
            identity: new UserIdentity(), // TODO: configuration.GetIdentity() default implementation: new UserIdentity()
            preferredLocales: null,
            cancellationToken).ConfigureAwait(false);

        var newSession = sessionResult as Session
            ?? throw new InvalidOperationException(
                $"Session factory returned unexpected type '{sessionResult?.GetType().FullName ?? "null"}'. " +
                $"Expected '{typeof(Session).FullName}'. Ensure the configured SessionFactory returns a valid Session instance.");

        // Enable SDK's built-in subscription transfer for seamless reconnection
        // TransferSubscriptionsOnReconnect: SDK will automatically transfer subscriptions during reconnect
        // DeleteSubscriptionsOnClose: Keep subscriptions on server during reconnection for transfer
        newSession.TransferSubscriptionsOnReconnect = true;
        newSession.DeleteSubscriptionsOnClose = false;

        // MinPublishRequestCount: Keep multiple publish requests in flight for reliability
        // OPC Foundation's reference client uses 3 to prevent message loss during traffic spikes
        newSession.MinPublishRequestCount = configuration.MinPublishRequestCount;

        newSession.KeepAlive -= OnKeepAlive;
        newSession.KeepAlive += OnKeepAlive;
        newSession.KeepAliveInterval = (int)configuration.KeepAliveInterval.TotalMilliseconds;

        Volatile.Write(ref _session, newSession);

        if (oldSession is not null)
        {
            await DisposeSessionAsync(oldSession, cancellationToken).ConfigureAwait(false);
        }

        return newSession;
    }

    public async Task CreateSubscriptionsAsync(
        IReadOnlyList<MonitoredItem> monitoredItems,
        Session session, CancellationToken cancellationToken)
    {
        await SubscriptionManager.CreateBatchedSubscriptionsAsync(monitoredItems, session, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Created {SubscriptionCount} subscriptions with {Subscribed} " +
            "total monitored items ({Polled} via polling).",
            SubscriptionManager.Subscriptions.Count,
            SubscriptionManager.MonitoredItems.Count,
            PollingManager?.PollingItemCount ?? 0);
    }

    /// <summary>
    /// Handles KeepAlive events and triggers automatic reconnection when connection is lost.
    /// </summary>
    private void OnKeepAlive(ISession sender, KeepAliveEventArgs e)
    {
        if (ServiceResult.IsGood(e.Status) ||
            e.CurrentState is not (ServerState.Unknown or ServerState.Failed))
        {
            return;
        }

        if (!Monitor.TryEnter(_reconnectingLock, 0))
        {
            _logger.LogDebug("OPC UA reconnect already in progress, skipping duplicate KeepAlive event.");
            return;
        }

        try
        {
            if (Volatile.Read(ref _disposed) == 1 ||
                Volatile.Read(ref _isReconnecting) == 1)
            {
                return;
            }

            var session = Volatile.Read(ref _session);
            if (session is null || !ReferenceEquals(sender, session))
            {
                return;
            }

            if (_reconnectHandler.State is not SessionReconnectHandler.ReconnectState.Ready)
            {
                _logger.LogWarning("OPC UA reconnect handler not ready. State: {State}.", _reconnectHandler.State);
                return;
            }

            _logger.LogInformation("OPC UA server connection lost. Beginning reconnect...");
            _propertyWriter.StartBuffering();

            // Set flag before BeginReconnect to avoid window where external observers see IsReconnecting=false
            Interlocked.Exchange(ref _isReconnecting, 1);

            try
            {
                var newState = _reconnectHandler.BeginReconnect(session, (int)_configuration.ReconnectInterval.TotalMilliseconds, OnReconnectComplete);
                if (newState is SessionReconnectHandler.ReconnectState.Triggered or SessionReconnectHandler.ReconnectState.Reconnecting)
                {
                    e.CancelKeepAlive = true;
                    _source.RecordReconnectionAttemptStart();
                }
                else
                {
                    // BeginReconnect failed - reset flag
                    Interlocked.Exchange(ref _isReconnecting, 0);
                    _logger.LogError("Failed to begin OPC UA reconnect. Handler state: {State}.", newState);
                }
            }
            catch (Exception ex)
            {
                // BeginReconnect threw - reset flag for immediate recovery instead of waiting 30s for stall detection
                Interlocked.Exchange(ref _isReconnecting, 0);
                _logger.LogError(ex, "BeginReconnect threw unexpected exception.");
            }
        }
        finally
        {
            Monitor.Exit(_reconnectingLock);
        }
    }

    private void OnReconnectComplete(object? sender, EventArgs e)
    {
        lock (_reconnectingLock)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            var reconnectedSession = _reconnectHandler.Session as Session;
            if (reconnectedSession is null)
            {
                _logger.LogWarning(
                    "Reconnect completed with null session. " +
                    "Clearing session to trigger full reconnection via health check.");

                AbandonCurrentSession();
                Interlocked.Exchange(ref _isReconnecting, 0);
                return;
            }

            var oldSession = Volatile.Read(ref _session);
            if (!ReferenceEquals(oldSession, reconnectedSession))
            {
                _logger.LogInformation("Reconnect created new OPC UA session.");

                // Check if subscriptions transferred BEFORE accepting the new session,
                // to avoid having two sessions that need disposal with only one pending slot.
                var transferredSubscriptions = reconnectedSession.Subscriptions.ToList();
                if (transferredSubscriptions.Count > 0)
                {
                    // Transfer succeeded - accept new session, defer old session disposal
                    if (oldSession is not null)
                    {
                        oldSession.KeepAlive -= OnKeepAlive;
                        _sessionsToDispose.Enqueue(oldSession);
                    }

                    Volatile.Write(ref _session, reconnectedSession);
                    reconnectedSession.KeepAlive -= OnKeepAlive;
                    reconnectedSession.KeepAlive += OnKeepAlive;

                    SubscriptionManager.UpdateTransferredSubscriptions(transferredSubscriptions);
                    _logger.LogInformation(
                        "OPC UA session reconnected: Transferred {Count} subscriptions. Health check will complete initialization.",
                        transferredSubscriptions.Count);
                    Interlocked.Exchange(ref _needsInitialization, 1);
                }
                else
                {
                    // Transfer failed - reject new session, abandon old session.
                    // The reconnected session has a live TCP connection so it must be disposed.
                    // The old session's connection is already dead (server restarted) and will be GC'd.
                    _logger.LogWarning(
                        "OPC UA session reconnected but subscription transfer failed (server restart). " +
                        "Clearing session to trigger full reconnection via health check.");
                    AbandonCurrentSession();
                    reconnectedSession.KeepAlive -= OnKeepAlive;
                    _sessionsToDispose.Enqueue(reconnectedSession);
                }
            }
            else
            {
                _logger.LogInformation("Reconnect preserved existing OPC UA session. Subscriptions maintained.");
                Interlocked.Exchange(ref _needsBufferResume, 1);
            }

            Interlocked.Exchange(ref _isReconnecting, 0);
        }
    }

    /// <summary>
    /// Attempts to force-reset if reconnection is stalled.
    /// Resets the SDK reconnect handler and clears state so health check can restart fresh.
    /// </summary>
    /// <returns>True if reset was performed, false otherwise.</returns>
    internal bool TryForceResetIfStalled()
    {
        lock (_reconnectingLock)
        {
            if (Volatile.Read(ref _isReconnecting) == 0)
            {
                return false;
            }

            // Reset the SDK reconnect handler to prevent it from interfering
            try
            {
                _reconnectHandler.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing stalled reconnect handler.");
            }

            _reconnectHandler = new SessionReconnectHandler(
                _configuration.TelemetryContext,
                false,
                (int)_configuration.ReconnectHandlerTimeout.TotalMilliseconds);

            // Clear everything - health check will restart fresh
            AbandonCurrentSession();
            Interlocked.Exchange(ref _isReconnecting, 0);
            return true;
        }
    }

    /// <summary>
    /// Disposes all sessions queued for deferred disposal.
    /// Called by health check loop after SDK reconnection completes.
    /// </summary>
    public async Task DisposePendingSessionsAsync(CancellationToken cancellationToken)
    {
        while (_sessionsToDispose.TryDequeue(out var session))
        {
            try
            {
                await DisposeSessionAsync(session, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing old session during reconnection cleanup.");
            }
        }
    }

    /// <summary>
    /// Clears the initialization flag after health check completes initialization.
    /// </summary>
    public void ClearInitializationFlag()
    {
        Interlocked.Exchange(ref _needsInitialization, 0);
    }

    /// <summary>
    /// Clears the buffer resume flag after the health check replays the buffer.
    /// </summary>
    public void ClearBufferResumeFlag()
    {
        Interlocked.Exchange(ref _needsBufferResume, 0);
    }

    /// <summary>
    /// Clears the current session and resets the reconnect handler to allow health check to trigger reconnection.
    /// Called when initialization fails after session creation.
    /// </summary>
    public async Task ClearSessionAsync(CancellationToken cancellationToken)
    {
        Session? sessionToDispose;

        lock (_reconnectingLock)
        {
            // Reset the reconnect handler to prevent it from trying to use the disposed session
            try
            {
                _reconnectHandler.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing reconnect handler during session clear.");
            }

            _reconnectHandler = new SessionReconnectHandler(
                _configuration.TelemetryContext,
                false,
                (int)_configuration.ReconnectHandlerTimeout.TotalMilliseconds);

            Interlocked.Exchange(ref _isReconnecting, 0);

            // Read and clear session inside lock to prevent race with OnReconnectComplete
            sessionToDispose = Volatile.Read(ref _session);
            Volatile.Write(ref _session, null);
            ReadAfterWriteManager?.ClearPendingReads();
        }

        // Dispose outside lock to avoid blocking SDK callbacks
        if (sessionToDispose is not null)
        {
            await DisposeSessionAsync(sessionToDispose, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Queues the current session for deferred disposal and clears session state
    /// so the health check loop will trigger a full reconnection.
    /// Must be called inside <see cref="_reconnectingLock"/>.
    /// </summary>
    private void AbandonCurrentSession()
    {
        Debug.Assert(Monitor.IsEntered(_reconnectingLock), "AbandonCurrentSession must be called inside _reconnectingLock.");

        var session = Volatile.Read(ref _session);
        if (session is not null)
        {
            session.KeepAlive -= OnKeepAlive;
            _sessionsToDispose.Enqueue(session);
        }

        Volatile.Write(ref _session, null);
        ReadAfterWriteManager?.ClearPendingReads();
    }

    private async Task DisposeSessionAsync(Session session, CancellationToken cancellationToken)
    {
        session.KeepAlive -= OnKeepAlive;
        try
        {
            await session.CloseAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("OPC UA session closed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing OPC UA session.");
        }
        try
        {
            session.Dispose();
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

        try { _reconnectHandler.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing reconnect handler."); }
        if (ReadAfterWriteManager is not null)
        {
            try { await ReadAfterWriteManager.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing read-after-write manager."); }
        }
        try { await SubscriptionManager.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing subscription manager."); }
        if (PollingManager is not null)
        {
            try { await PollingManager.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing polling manager."); }
        }

        // Dispose all sessions queued for deferred disposal
        while (_sessionsToDispose.TryDequeue(out var pendingSession))
        {
            try { pendingSession.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing pending old session."); }
        }

        var sessionToDispose = Volatile.Read(ref _session);
        if (sessionToDispose is not null)
        {
            var timeout = _configuration.SessionDisposalTimeout;
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await DisposeSessionAsync(sessionToDispose, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Session disposal timed out after {Timeout} during shutdown. " +
                    "Forcing synchronous disposal to complete cleanup.",
                    timeout);

                // Force immediate disposal without waiting for server acknowledgment
                try { sessionToDispose.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Error force-disposing session."); }
            }

            Volatile.Write(ref _session, null);
        }
    }

    /// <summary>
    /// Satisfies IDisposable for interface compatibility.
    /// Delegates to DisposeAsync() via fire-and-forget to ensure cleanup.
    /// SubjectSourceBackgroundService checks for IAsyncDisposable first, so this is never called in normal operation.
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
