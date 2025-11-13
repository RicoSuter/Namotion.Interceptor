using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.Sources;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Client.Connection;

/// <summary>
/// Manages OPC UA session lifecycle, reconnection handling, and thread-safe session access.
/// Optimized for fast session reads (hot path) with simple lock-based writes (cold path).
/// </summary>
internal sealed class SessionManager : IDisposable, IAsyncDisposable
{
    private readonly ISubjectUpdater _updater;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;

    private readonly SubscriptionManager _subscriptionManager;
    private readonly SessionReconnectHandler _reconnectHandler;
    private readonly PollingManager? _pollingManager;

    private Session? _session;
    private CancellationToken _stoppingToken;

    private readonly object _reconnectingLock = new();

    private int _isReconnecting; // 0 = false, 1 = true (thread-safe via Interlocked)
    private int _disposed; // 0 = false, 1 = true (thread-safe via Interlocked)

    /// <summary>
    /// Gets the current session. WARNING: Can change at any time due to reconnection. Never cache - read immediately before use.
    /// </summary>
    public Session? CurrentSession => Volatile.Read(ref _session);

    public bool IsConnected => Volatile.Read(ref _session) is not null;

    public bool IsReconnecting => Interlocked.CompareExchange(ref _isReconnecting, 0, 0) == 1;

    public IReadOnlyCollection<Subscription> Subscriptions => _subscriptionManager.Subscriptions;

    /// <summary>
    /// Fires when reconnection completes successfully. Invoked on background thread.
    /// </summary>
    public event EventHandler? ReconnectionCompleted;

    public SessionManager(ISubjectUpdater updater, OpcUaClientConfiguration configuration, ILogger logger)
    {
        _updater = updater;
        _logger = logger;
        _configuration = configuration;
        _reconnectHandler = new SessionReconnectHandler(false, (int)configuration.ReconnectHandlerTimeout);

        if (_configuration.EnablePollingFallback)
        {
            _pollingManager = new PollingManager(
                logger: _logger,
                sessionManager: this,
                updater: updater,
                pollingInterval: _configuration.PollingInterval,
                batchSize: _configuration.PollingBatchSize,
                disposalTimeout: _configuration.PollingDisposalTimeout,
                circuitBreakerThreshold: _configuration.PollingCircuitBreakerThreshold,
                circuitBreakerCooldown: _configuration.PollingCircuitBreakerCooldown
            );

            _pollingManager.Start();
        }

        _subscriptionManager = new SubscriptionManager(updater, _pollingManager, configuration, logger);
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
        var endpointDescription = CoreClientUtils.SelectEndpoint(
            application.ApplicationConfiguration,
            configuration.ServerUrl,
            useSecurity: false);

        var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);
        var oldSession = Volatile.Read(ref _session);

        var newSession = await Session.Create(
            application.ApplicationConfiguration,
            endpoint,
            updateBeforeConnect: false,
            application.ApplicationName,
            sessionTimeout: configuration.SessionTimeout,
            new UserIdentity(),
            preferredLocales: null,
            cancellationToken).ConfigureAwait(false);

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
        // This method will never be called concurrently, so no lock is needed here.

        _stoppingToken = cancellationToken;

        await _subscriptionManager.CreateBatchedSubscriptionsAsync(monitoredItems, session, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Created {SubscriptionCount} subscriptions with {Subscribed} " +
            "total monitored items ({Polled} via polling).",
            _subscriptionManager.Subscriptions.Count,
            _subscriptionManager.MonitoredItems.Count,
            _pollingManager?.PollingItemCount ?? 0);
    }

    /// <summary>
    /// Handles KeepAlive events and triggers automatic reconnection when connection is lost.
    /// </summary>
    private void OnKeepAlive(ISession sender, KeepAliveEventArgs e)
    {
        if (ServiceResult.IsGood(e.Status))
        {
            return;
        }

        if (e.CurrentState is not (ServerState.Unknown or ServerState.Failed))
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
            if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1 ||
                Interlocked.CompareExchange(ref _isReconnecting, 0, 0) == 1)
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
                _logger.LogWarning("OPC UA SessionReconnectHandler not ready. State: {State}", _reconnectHandler.State);
                return;
            }

            _logger.LogInformation("OPC UA server connection lost. Beginning reconnect...");

            // Start collecting updates before reconnection - any incoming notifications will be buffered
            _updater.StartCollectingUpdates();

            var newState = _reconnectHandler.BeginReconnect(session, _configuration.ReconnectInterval, OnReconnectComplete);
            if (newState is SessionReconnectHandler.ReconnectState.Triggered or SessionReconnectHandler.ReconnectState.Reconnecting)
            {
                Interlocked.Exchange(ref _isReconnecting, 1);
                e.CancelKeepAlive = true;
            }
            else
            {
                _logger.LogError("Failed to begin OPC UA reconnect. Handler state: {State}", newState);
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
            if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
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
                        _subscriptionManager.UpdateTransferredSubscriptions(transferredSubscriptions);
                        _logger.LogInformation("OPC UA session reconnected: Transferred {Count} subscriptions.", transferredSubscriptions.Count);
                    }
                }

                if (oldSession is not null && !ReferenceEquals(oldSession, newSession))
                {
                    Task.Run(() => DisposeSessionAsync(oldSession, _stoppingToken));
                }
            }
            else
            {
                _logger.LogInformation("Reconnect preserved existing OPC UA session. Subscriptions maintained.");
            }

            reconnectionSucceeded = true;
            Interlocked.Exchange(ref _isReconnecting, 0);
        }

        // Only fire event if reconnection actually succeeded
        if (reconnectionSucceeded)
        {
            ReconnectionCompleted?.Invoke(this, EventArgs.Empty);
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
            // If OnReconnectComplete fired while we were waiting for the lock, it would have:
            // 1. Set session to non-null (line 216)
            // 2. Set _isReconnecting = 0 (line 242)
            if (Interlocked.CompareExchange(ref _isReconnecting, 0, 0) == 1 &&
                Volatile.Read(ref _session) is null)
            {
                // Truly stalled - OnReconnectComplete never fired or failed
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
        // Unsubscribe from event (standard event -= operator cannot throw under normal circumstances)
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

        // Dispose managers FIRST (children) - they reference the session
        // Proper cleanup order: children before parent
        _subscriptionManager.Dispose();       // Clean up subscription references on session
        _pollingManager?.Dispose();           // Stop polling operations
        _reconnectHandler.Dispose();          // Stop reconnection attempts

        // Then dispose session LAST (parent)
        var sessionToDispose = _session;
        if (sessionToDispose is not null)
        {
            await DisposeSessionAsync(sessionToDispose, CancellationToken.None).ConfigureAwait(false);
            _session = null;
        }
    }

    public void Dispose()
    {
        DisposeAsync().GetAwaiter().GetResult();
    }
}