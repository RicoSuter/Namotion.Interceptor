using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.Sources;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Manages OPC UA session lifecycle, reconnection handling, and thread-safe session access.
/// Optimized for fast session reads (hot path) with simple lock-based writes (cold path).
/// </summary>
internal sealed class OpcUaSessionManager : IDisposable, IAsyncDisposable
{
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;

    private readonly OpcUaSubscriptionManager _subscriptionManager;
    private readonly SessionReconnectHandler _reconnectHandler;
    private readonly PollingManager? _pollingManager;

    private Session? _session;
    private CancellationToken _stoppingToken;

    private readonly object _reconnectingLock = new();

    private int _isReconnecting; // 0 = false, 1 = true (thread-safe via Interlocked)
    private int _disposed; // 0 = false, 1 = true (thread-safe via Interlocked)

    /// <summary>
    /// Gets the current session, or null if not connected.
    /// <para>
    /// <strong>Thread-Safety:</strong> Thread-safe for reading without lock using volatile semantics for memory visibility.
    /// WARNING: The session reference can change at any time due to reconnection. Do not cache this value.
    /// Always read CurrentSession immediately before use, and handle null gracefully.
    /// </para>
    /// </summary>
    public Session? CurrentSession => Volatile.Read(ref _session);

    /// <summary>
    /// Gets whether a session is currently connected.
    /// Thread-safe using volatile semantics for memory visibility.
    /// </summary>
    public bool IsConnected => Volatile.Read(ref _session) is not null;

    /// <summary>
    /// Gets whether a reconnection is in progress.
    /// Thread-safe via Interlocked operations.
    /// </summary>
    public bool IsReconnecting => Interlocked.CompareExchange(ref _isReconnecting, 0, 0) == 1;

    /// <summary>
    /// Gets the current list of subscriptions.
    /// Thread-safe - returns a snapshot of subscriptions.
    /// </summary>
    public IReadOnlyCollection<Subscription> Subscriptions => _subscriptionManager.Subscriptions;

    /// <summary>
    /// Occurs when a reconnection attempt completes (successfully or not).
    /// <para>
    /// <strong>Thread-Safety:</strong> This event is invoked on a background thread (reconnection handler thread).
    /// Event handlers MUST be thread-safe and should not perform blocking operations.
    /// </para>
    /// </summary>
    public event EventHandler? ReconnectionCompleted;

    public OpcUaSessionManager(ISubjectUpdater updater, OpcUaClientConfiguration configuration, ILogger logger)
    {
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

        _subscriptionManager = new OpcUaSubscriptionManager(updater, _pollingManager, configuration, logger);
    }

    /// <summary>
    /// Create a new OPC UA session with the specified configuration.
    /// </summary>
    public async Task<Session> CreateSessionAsync(
        ApplicationInstance application,
        OpcUaClientConfiguration configuration,
        CancellationToken cancellationToken)
    {
        // This method will never be called concurrently, so no lock is needed here.
        
        var endpointConfiguration = EndpointConfiguration.Create(application.ApplicationConfiguration);
        var endpointDescription = CoreClientUtils.SelectEndpoint(
            application.ApplicationConfiguration,
            configuration.ServerUrl,
            useSecurity: false);

        var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);
        var oldSession = _session;
        
        _session = await Session.Create(
            application.ApplicationConfiguration,
            endpoint,
            updateBeforeConnect: false,
            application.ApplicationName,
            sessionTimeout: configuration.SessionTimeout,
            new UserIdentity(),
            preferredLocales: null,
            cancellationToken).ConfigureAwait(false);

        _session.KeepAlive += OnKeepAlive;

        if (oldSession is not null)
        {
            await DisposeSessionAsync(oldSession, cancellationToken).ConfigureAwait(false);
        }

        return _session;
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

            if (_session is not { } session || !ReferenceEquals(sender, session))
            {
                return;
            }

            // Pre-check handler state to avoid unnecessary BeginReconnect calls (optimization only)
            // Note: State could change between check and BeginReconnect call, but this is safe because
            // we validate the return value from BeginReconnect and only set _isReconnecting on success.
            if (_reconnectHandler.State is not SessionReconnectHandler.ReconnectState.Ready)
            {
                _logger.LogWarning("OPC UA SessionReconnectHandler not ready. State: {State}", _reconnectHandler.State);
                return;
            }

            _logger.LogInformation("OPC UA server connection lost. Beginning reconnect...");

            // Return value is authoritative - only set _isReconnecting if BeginReconnect succeeds
            var newState = _reconnectHandler.BeginReconnect(session, _configuration.ReconnectInterval, OnReconnectComplete);
            if (newState is SessionReconnectHandler.ReconnectState.Triggered or SessionReconnectHandler.ReconnectState.Reconnecting)
            {
                Interlocked.Exchange(ref _isReconnecting, 1);
                e.CancelKeepAlive = true;
            }
            else
            {
                // BeginReconnect failed - don't set _isReconnecting, allow retry on next KeepAlive
                _logger.LogError("Failed to begin OPC UA reconnect. Handler state: {State}", newState);
            }
        }
        finally
        {
            Monitor.Exit(_reconnectingLock);
        }
    }

    /// <summary>
    /// Callback invoked by SessionReconnectHandler when reconnection completes.
    /// Uses synchronous continuation to avoid fire-and-forget anti-pattern.
    /// </summary>
    private void OnReconnectComplete(object? sender, EventArgs e)
    {
        bool reconnectionSucceeded = false;

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
                return; // Don't fire event - reconnection failed
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
                    newSession.KeepAlive -= OnKeepAlive; // Defensive
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
                    // Task.Run is safe here: DisposeSessionAsync handles all exceptions internally
                    // No unobserved exceptions possible - all operations are try-catch wrapped
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
        
        var sessionToDispose = _session;
        if (sessionToDispose is not null)
        {
            await DisposeSessionAsync(sessionToDispose, CancellationToken.None).ConfigureAwait(false);
            _session = null;
        }

        _pollingManager?.Dispose();
        _subscriptionManager.Dispose();
        _reconnectHandler.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().GetAwaiter().GetResult();
    }
}