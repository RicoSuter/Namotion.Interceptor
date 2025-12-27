using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
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
    private readonly SubjectPropertyWriter _propertyWriter;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;

    private readonly SessionReconnectHandler _reconnectHandler;

    private Session? _session;
    private CancellationToken _stoppingToken;

    private readonly object _reconnectingLock = new();

    private int _isReconnecting; // 0 = false, 1 = true (thread-safe via Interlocked)
    private int _disposed; // 0 = false, 1 = true (thread-safe via Interlocked)

    /// <summary>
    /// Gets the current session. WARNING: Can change at any time due to reconnection. Never cache - read immediately before use.
    /// </summary>
    public Session? CurrentSession => Volatile.Read(ref _session);

    /// <summary>
    /// Gets a value indicating whether the session is currently connected.
    /// </summary>
    public bool IsConnected => Volatile.Read(ref _session) is not null;

    /// <summary>
    /// Gets a value indicating whether the session is currently reconnecting.
    /// </summary>
    public bool IsReconnecting => Volatile.Read(ref _isReconnecting) == 1;

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

    public SessionManager(OpcUaSubjectClientSource source, SubjectPropertyWriter propertyWriter, OpcUaClientConfiguration configuration, ILogger logger)
    {
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

        SubscriptionManager = new SubscriptionManager(source, propertyWriter, PollingManager, configuration, logger);
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
            useSecurity: false,
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
            identity: new UserIdentity(),
            preferredLocales: null,
            cancellationToken).ConfigureAwait(false);

        var newSession = sessionResult as Session
            ?? throw new InvalidOperationException(
                $"Session factory returned unexpected type '{sessionResult?.GetType().FullName ?? "null"}'. " +
                $"Expected '{typeof(Session).FullName}'. Ensure the configured SessionFactory returns a valid Session instance.");

        newSession.KeepAlive -= OnKeepAlive;
        newSession.KeepAlive += OnKeepAlive;

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
        _stoppingToken = cancellationToken;

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

            var newState = _reconnectHandler.BeginReconnect(session, (int)_configuration.ReconnectInterval.TotalMilliseconds, OnReconnectComplete);
            if (newState is SessionReconnectHandler.ReconnectState.Triggered or SessionReconnectHandler.ReconnectState.Reconnecting)
            {
                Interlocked.Exchange(ref _isReconnecting, 1);
                e.CancelKeepAlive = true;
            }
            else
            {
                _logger.LogError("Failed to begin OPC UA reconnect. Handler state: {State}.", newState);
            }
        }
        finally
        {
            Monitor.Exit(_reconnectingLock);
        }
    }

    private void OnReconnectComplete(object? sender, EventArgs e)
    {
        bool reconnectionSucceeded;
        lock (_reconnectingLock)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            var reconnectedSession = _reconnectHandler.Session;
            if (reconnectedSession is null)
            {
                _logger.LogError("Reconnect completed but received null OPC UA session. Will retry on next KeepAlive.");
                Interlocked.Exchange(ref _isReconnecting, 0);
                return;
            }

            var oldSession = _session;
            var isNewSession = !ReferenceEquals(_session, reconnectedSession);
            if (isNewSession)
            {
                _logger.LogInformation("Reconnect created new OPC UA session.");

                var newSession = reconnectedSession as Session;
                Volatile.Write(ref _session, newSession);

                if (newSession is not null)
                {
                    newSession.KeepAlive -= OnKeepAlive;
                    newSession.KeepAlive += OnKeepAlive;

                    var transferredSubscriptions = newSession.Subscriptions.ToList();
                    if (transferredSubscriptions.Count > 0)
                    {
                        SubscriptionManager.UpdateTransferredSubscriptions(transferredSubscriptions);
                        _logger.LogInformation("OPC UA session reconnected: Transferred {Count} subscriptions.", transferredSubscriptions.Count);
                    }
                }

                if (oldSession is not null && !ReferenceEquals(oldSession, newSession))
                {
                    Task.Run(() => DisposeSessionAsync(oldSession, _stoppingToken), _stoppingToken);
                }
            }
            else
            {
                _logger.LogInformation("Reconnect preserved existing OPC UA session. Subscriptions maintained.");
            }

            reconnectionSucceeded = true;
            Interlocked.Exchange(ref _isReconnecting, 0);
        }

        if (reconnectionSucceeded)
        {
            Task.Run(async () =>
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    return; // Already disposing, skip reconnection work
                }
                var session = CurrentSession;
                if (session is not null)
                {
                    try
                    {
                        await _propertyWriter.CompleteInitializationAsync(_stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception, "Reconnect failed, closing session. Health check will trigger full restart.");
                        await DisposeSessionAsync(session, _stoppingToken).ConfigureAwait(false);

                        // Only clear session if it's still the same one we tried to initialize
                        // Prevents race with concurrent reconnection or disposal
                        var currentSession = Volatile.Read(ref _session);
                        if (ReferenceEquals(currentSession, session))
                        {
                            Volatile.Write(ref _session, null);
                        }
                    }
                }
            }, _stoppingToken);
            
            _logger.LogInformation("OPC UA session reconnect completed.");
        }
    }

    /// <summary>
    /// Attempts to force-reset the reconnecting flag if reconnection is truly stalled.
    /// Uses lock and double-check to prevent race with delayed OnReconnectComplete.
    /// </summary>
    /// <returns>True if flag was reset (stall confirmed), false if reconnection completed while waiting.</returns>
    internal bool TryForceResetIfStalled()
    {
        lock (_reconnectingLock)
        {
            // Double-check: still reconnecting AND session still null?
            if (Volatile.Read(ref _isReconnecting) == 1 &&
                Volatile.Read(ref _session) is null)
            {
                // Truly stalled - OnReconnectComplete never fired or failed:
                // Safe to clear flag and allow manual recovery
                Interlocked.Exchange(ref _isReconnecting, 0);
                return true;
            }

            // Reconnection completed while we were waiting for lock - do nothing
            return false;
        }
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

        try { _reconnectHandler.Dispose(); } catch { /* best effort */ }
        try { await SubscriptionManager.DisposeAsync().ConfigureAwait(false); } catch { /* best effort */ }
        try { PollingManager?.Dispose(); } catch { /* best effort */ }

        var sessionToDispose = _session;
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
                try { sessionToDispose.Dispose(); } catch { /* best effort */ }
            }

            Volatile.Write(ref _session, null);
        }
    }

    /// <summary>
    /// Synchronous dispose - prefer DisposeAsync() for proper cleanup.
    /// This is only called if the caller doesn't check for IAsyncDisposable.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try { _reconnectHandler.Dispose(); } catch { /* best effort */ }
        try { PollingManager?.Dispose(); } catch { /* best effort */ }

        var sessionToDispose = _session;
        if (sessionToDispose is not null)
        {
            sessionToDispose.KeepAlive -= OnKeepAlive;
            try { sessionToDispose.Dispose(); } catch { /* best effort */ }
            Volatile.Write(ref _session, null);
        }
    }
}
