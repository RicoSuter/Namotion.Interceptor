using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Manages OPC UA session lifecycle, reconnection handling, and thread-safe session access.
/// Optimized for fast session reads (hot path) with simple lock-based writes (cold path).
/// </summary>
internal sealed class OpcUaSessionManager : IDisposable
{
    private readonly object _lock = new();
    private Session? _session;
    private readonly SessionReconnectHandler _reconnectHandler;
    private readonly ILogger _logger;
    private bool _isReconnecting;
    private bool _disposed;

    /// <summary>
    /// Gets the current session, or null if not connected.
    /// Thread-safe for reading without lock (session assignment is atomic).
    /// </summary>
    public Session? CurrentSession => _session;

    /// <summary>
    /// Gets whether a session is currently connected.
    /// </summary>
    public bool IsConnected => _session is not null;

    /// <summary>
    /// Gets whether a reconnection is in progress.
    /// </summary>
    public bool IsReconnecting
    {
        get { lock (_lock) return _isReconnecting; }
    }

    /// <summary>
    /// Occurs when the session changes (new session, reconnected, or disconnected).
    /// </summary>
    public event EventHandler<SessionChangedEventArgs>? SessionChanged;

    /// <summary>
    /// Occurs when a reconnection attempt completes (successfully or not).
    /// </summary>
    public event EventHandler? ReconnectionCompleted;

    public OpcUaSessionManager(ILogger logger)
    {
        _logger = logger;
        _reconnectHandler = new SessionReconnectHandler(false, 60000);
    }

    /// <summary>
    /// Creates a new OPC UA session with the specified configuration.
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

        var newSession = await Session.Create(
            application.ApplicationConfiguration,
            endpoint,
            updateBeforeConnect: false,
            application.ApplicationName,
            sessionTimeout: 60000,
            new UserIdentity(),
            preferredLocales: null,
            cancellationToken);

        Session? oldSession;
        lock (_lock)
        {
            oldSession = _session;
            _session = newSession;
            _isReconnecting = false;

            newSession.KeepAlive += OnKeepAlive;
        }

        // Dispose old session outside lock
        if (oldSession is not null)
        {
            oldSession.KeepAlive -= OnKeepAlive;
            await DisposeSessionSafelyAsync(oldSession);
        }

        SessionChanged?.Invoke(this, new SessionChangedEventArgs(newSession, isNewSession: true));
        return newSession;
    }

    /// <summary>
    /// Handles KeepAlive events and triggers automatic reconnection when connection is lost.
    /// </summary>
    private void OnKeepAlive(ISession sender, KeepAliveEventArgs e)
    {
        if (ServiceResult.IsGood(e.Status))
            return;

        _logger.LogWarning("KeepAlive failed with status: {Status}. Connection may be lost", e.Status);

        if (e.CurrentState is not (ServerState.Unknown or ServerState.Failed))
            return;

        // Use Monitor.TryEnter to avoid blocking OPC UA stack thread
        if (!Monitor.TryEnter(_lock, TimeSpan.FromMilliseconds(100)))
        {
            _logger.LogWarning("Could not acquire lock for reconnect. Will retry on next KeepAlive");
            return;
        }

        try
        {
            if (_disposed || _isReconnecting)
                return;

            if (_session is not { } session || !ReferenceEquals(sender, session))
                return;

            if (_reconnectHandler.State is not SessionReconnectHandler.ReconnectState.Ready)
            {
                _logger.LogWarning("SessionReconnectHandler not ready. State: {State}", _reconnectHandler.State);
                return;
            }

            _logger.LogInformation("Server connection lost. Beginning reconnect with exponential backoff");

            var newState = _reconnectHandler.BeginReconnect(session, 5000, OnReconnectComplete);

            if (newState is SessionReconnectHandler.ReconnectState.Triggered or SessionReconnectHandler.ReconnectState.Reconnecting)
            {
                _isReconnecting = true;
                e.CancelKeepAlive = true;
                _logger.LogInformation("Reconnect handler initiated successfully");
            }
            else
            {
                _logger.LogError("Failed to begin reconnect. Handler state: {State}", newState);
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    /// <summary>
    /// Callback invoked by SessionReconnectHandler when reconnection completes.
    /// Queues async work since this is a synchronous callback.
    /// </summary>
    private void OnReconnectComplete(object? sender, EventArgs e)
    {
        _ = HandleReconnectCompleteAsync();
    }

    private async Task HandleReconnectCompleteAsync()
    {
        Session? oldSession = null;
        Session? newSession = null;
        bool isNewSession;

        lock (_lock)
        {
            if (_disposed)
                return;

            var reconnectedSession = _reconnectHandler.Session;
            if (reconnectedSession is null)
            {
                _logger.LogError("Reconnect completed but received null session. Connection lost permanently");
                _isReconnecting = false;
                SessionChanged?.Invoke(this, new SessionChangedEventArgs(null, isNewSession: false));
                return;
            }

            isNewSession = !ReferenceEquals(_session, reconnectedSession);

            if (isNewSession)
            {
                _logger.LogInformation("Reconnect created new session. Subscriptions transferred by OPC UA stack");
                oldSession = _session;
                newSession = reconnectedSession as Session;
                _session = newSession;

                if (newSession is not null)
                {
                    newSession.KeepAlive -= OnKeepAlive; // Defensive
                    newSession.KeepAlive += OnKeepAlive;
                }
            }
            else
            {
                _logger.LogInformation("Reconnect preserved existing session. Subscriptions maintained");
            }

            _isReconnecting = false;
        }

        // Dispose old session outside lock
        if (oldSession is not null && !ReferenceEquals(oldSession, newSession))
        {
            oldSession.KeepAlive -= OnKeepAlive;
            await DisposeSessionSafelyAsync(oldSession);
        }

        if (isNewSession)
        {
            SessionChanged?.Invoke(this, new SessionChangedEventArgs(newSession, isNewSession: true));
        }

        ReconnectionCompleted?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation("Session reconnect completed. New session: {IsNew}", isNewSession);
    }

    /// <summary>
    /// Closes the current session.
    /// </summary>
    public async Task CloseSessionAsync()
    {
        Session? sessionToClose;
        lock (_lock)
        {
            sessionToClose = _session;
            if (sessionToClose is not null)
            {
                sessionToClose.KeepAlive -= OnKeepAlive;
                _session = null;
                _isReconnecting = false;
            }
        }

        if (sessionToClose is not null)
        {
            await DisposeSessionSafelyAsync(sessionToClose);
        }
    }

    private async Task DisposeSessionSafelyAsync(Session session)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await session.CloseAsync(cts.Token);
            session.Dispose();
            _logger.LogDebug("Session disposed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing session");
            try { session.Dispose(); } catch { /* Best effort */ }
        }
    }

    public void Dispose()
    {
        Session? sessionToDispose;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;

            sessionToDispose = _session;
            if (sessionToDispose is not null)
            {
                sessionToDispose.KeepAlive -= OnKeepAlive;
                _session = null;
            }
        }

        if (sessionToDispose is not null)
        {
            // Synchronous disposal in Dispose() - acceptable
            try
            {
                sessionToDispose.Close(2000);
                sessionToDispose.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing session in Dispose");
                try { sessionToDispose.Dispose(); } catch { /* Best effort */ }
            }
        }

        _reconnectHandler?.Dispose();
    }
}

/// <summary>
/// Event args for session change events.
/// </summary>
internal sealed class SessionChangedEventArgs(Session? session, bool isNewSession) : EventArgs
{
    public Session? Session { get; } = session;
    public bool IsNewSession { get; } = isNewSession;
}
