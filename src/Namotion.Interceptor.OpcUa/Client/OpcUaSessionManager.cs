using Microsoft.Extensions.Logging;
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
    private readonly object _lock = new();

    private Session? _session;
    private readonly SessionReconnectHandler _reconnectHandler;
    private readonly ILogger _logger;
    private readonly OpcUaClientConfiguration _configuration;
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
    /// Occurs when the session changes (new session, reconnected, or disconnected).
    /// <para>
    /// <strong>Thread-Safety:</strong> This event is invoked on a background thread (reconnection handler thread or session creation thread).
    /// Event handlers MUST be thread-safe and should not perform blocking operations.
    /// The session object may change concurrently - handlers should not cache session references without proper synchronization.
    /// </para>
    /// </summary>
    public event EventHandler<SessionChangedEventArgs>? SessionChanged;

    /// <summary>
    /// Occurs when a reconnection attempt completes (successfully or not).
    /// <para>
    /// <strong>Thread-Safety:</strong> This event is invoked on a background thread (reconnection handler thread).
    /// Event handlers MUST be thread-safe and should not perform blocking operations.
    /// Handlers should use proper synchronization when accessing shared state.
    /// </para>
    /// </summary>
    public event EventHandler? ReconnectionCompleted;

    public OpcUaSessionManager(ILogger logger, OpcUaClientConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _reconnectHandler = new SessionReconnectHandler(false, (int)configuration.ReconnectHandlerTimeout);
    }

    /// <summary>
    /// Create a new OPC UA session with the specified configuration.
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
            sessionTimeout: configuration.SessionTimeout,
            new UserIdentity(),
            preferredLocales: null,
            cancellationToken);

        Session? oldSession;
        lock (_lock)
        {
            oldSession = _session;

            // Unregister old session handler inside lock to prevent race condition
            if (oldSession is not null)
            {
                oldSession.KeepAlive -= OnKeepAlive;
            }

            Volatile.Write(ref _session, newSession);
            Interlocked.Exchange(ref _isReconnecting, 0);

            newSession.KeepAlive += OnKeepAlive;
        }

        // Dispose old session outside lock
        if (oldSession is not null)
        {
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
        {
            return;
        }

        if (e.CurrentState is not (ServerState.Unknown or ServerState.Failed))
        {
            return;
        }

        // Use non-blocking lock acquisition to avoid cascading KeepAlive thread pool exhaustion
        // If lock is held (e.g., CreateSessionAsync or concurrent reconnect), skip this event - the next KeepAlive will retry
        if (!Monitor.TryEnter(_lock, 0))
        {
            _logger.LogDebug("Reconnect already in progress, skipping duplicate KeepAlive event");
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

            if (_reconnectHandler.State is not SessionReconnectHandler.ReconnectState.Ready)
            {
                _logger.LogWarning("SessionReconnectHandler not ready. State: {State}", _reconnectHandler.State);
                return;
            }

            _logger.LogInformation("Server connection lost. Beginning reconnect with exponential backoff");

            var newState = _reconnectHandler.BeginReconnect(session, _configuration.ReconnectInterval, OnReconnectComplete);
            if (newState is SessionReconnectHandler.ReconnectState.Triggered or SessionReconnectHandler.ReconnectState.Reconnecting)
            {
                Interlocked.Exchange(ref _isReconnecting, 1);
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
    /// Uses synchronous continuation to avoid fire-and-forget anti-pattern.
    /// </summary>
    private void OnReconnectComplete(object? sender, EventArgs e)
    {
        HandleReconnectCompleteAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.LogError(t.Exception, "Unhandled exception in reconnect completion handler");
            }
        }, TaskScheduler.Default);
    }

    private async Task HandleReconnectCompleteAsync()
    {
        Session? oldSession = null;
        Session? newSession = null;
        bool isNewSession;

        lock (_lock)
        {
            if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
            {
                return;
            }

            var reconnectedSession = _reconnectHandler.Session;
            if (reconnectedSession is null)
            {
                _logger.LogError("Reconnect completed but received null OPC UA session. Connection lost permanently.");
                Interlocked.Exchange(ref _isReconnecting, 0);

                SessionChanged?.Invoke(this, new SessionChangedEventArgs(null, isNewSession: false));
                return;
            }

            isNewSession = !ReferenceEquals(_session, reconnectedSession);
            if (isNewSession)
            {
                _logger.LogInformation("Reconnect created new OPC UA session. Subscriptions transferred by OPC UA stack.");
                oldSession = _session;
                newSession = reconnectedSession as Session;
                Volatile.Write(ref _session, newSession);

                if (newSession is not null)
                {
                    newSession.KeepAlive -= OnKeepAlive; // Defensive
                    newSession.KeepAlive += OnKeepAlive;
                }
            }
            else
            {
                _logger.LogInformation("Reconnect preserved existing OPC UA session. Subscriptions maintained.");
            }

            Interlocked.Exchange(ref _isReconnecting, 0);
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
        _logger.LogInformation("OPC UA session reconnect completed. New session: {IsNew}", isNewSession);
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
                Volatile.Write(ref _session, null);
                Interlocked.Exchange(ref _isReconnecting, 0);
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
            using var cts = new CancellationTokenSource(_configuration.SessionDisposalTimeout);
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

    public async ValueTask DisposeAsync()
    {
        // Set disposed flag first to prevent new operations (thread-safe)
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // Already disposed
        }

        Session? sessionToDispose;
        lock (_lock)
        {
            sessionToDispose = _session;
            if (sessionToDispose is not null)
            {
                sessionToDispose.KeepAlive -= OnKeepAlive;
                Volatile.Write(ref _session, null);
            }
        }

        if (sessionToDispose is not null)
        {
            await DisposeSessionSafelyAsync(sessionToDispose);
        }

        try
        {
            _reconnectHandler?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing reconnect handler");
        }
    }

    public void Dispose()
    {
        // Synchronous dispose uses async-over-sync with timeout to prevent hanging
        // This is acceptable for disposal as it's typically called during shutdown
        try
        {
            DisposeAsync().AsTask().Wait(_configuration.SessionDisposalTimeout);
        }
        catch (AggregateException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogWarning("Session disposal timed out after {Timeout}. Forcing disposal.", _configuration.SessionDisposalTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during synchronous disposal");
        }
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
