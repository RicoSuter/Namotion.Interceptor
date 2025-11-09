using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Manages OPC UA session lifecycle, reconnection handling, and thread-safe session access.
/// </summary>
internal sealed class OpcUaSessionManager : IDisposable
{
    private Session? _session;
    private readonly SessionReconnectHandler _reconnectHandler;
    private readonly SemaphoreSlim _sessionSemaphore = new(1, 1);
    private readonly ILogger _logger;
    private int _isReconnecting = 0; // 0 = false, 1 = true (use Interlocked)
    private int _disposed = 0; // 0 = not disposed, 1 = disposed

    /// <summary>
    /// Gets the current session, or null if not connected.
    /// </summary>
    public Session? CurrentSession => _session;

    /// <summary>
    /// Gets whether a session is currently connected.
    /// </summary>
    public bool IsConnected => _session != null;

    /// <summary>
    /// Gets whether a reconnection is in progress.
    /// </summary>
    public bool IsReconnecting => Interlocked.CompareExchange(ref _isReconnecting, 0, 0) == 1;

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

        // Initialize SessionReconnectHandler with exponential backoff (max 60s)
        // First parameter: false = preserve session when possible (don't always close)
        // Second parameter: 60000ms = max reconnect period for exponential backoff
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
            false);
        var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

        await _sessionSemaphore.WaitAsync(cancellationToken);
        try
        {
            var newSession = await Session.Create(
                application.ApplicationConfiguration,
                endpoint,
                false,
                application.ApplicationName,
                60000,
                new UserIdentity(),
                null,
                cancellationToken);

            var oldSession = _session;
            _session = newSession;
            Interlocked.Exchange(ref _isReconnecting, 0);

            // Setup KeepAlive event handler for automatic reconnection
            newSession.KeepAlive += OnKeepAlive;

            // Clean up old session if exists
            if (oldSession != null)
            {
                oldSession.KeepAlive -= OnKeepAlive;

                // Dispose old session asynchronously - use short timeout to avoid blocking
                using var disposeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                disposeCts.CancelAfter(TimeSpan.FromSeconds(2));
                await DisposeSessionSafelyAsync(oldSession, disposeCts.Token);
            }

            SessionChanged?.Invoke(this, new SessionChangedEventArgs(newSession, true));

            return newSession;
        }
        finally
        {
            _sessionSemaphore.Release();
        }
    }

    /// <summary>
    /// Handles KeepAlive events and triggers automatic reconnection when connection is lost.
    /// </summary>
    private void OnKeepAlive(ISession sender, KeepAliveEventArgs e)
    {
        // Only handle bad status - good status means connection is healthy
        if (ServiceResult.IsGood(e.Status))
        {
            return;
        }

        _logger.LogWarning("KeepAlive failed with status: {Status}. Connection may be lost.", e.Status);

        // Critical connection states that require reconnection
        if (e.CurrentState == ServerState.Unknown || e.CurrentState == ServerState.Failed)
        {
            // Use timeout to prevent blocking OPC UA stack thread
            if (!_sessionSemaphore.Wait(TimeSpan.FromMilliseconds(100)))
            {
                _logger.LogWarning("Could not acquire semaphore for reconnect within timeout. Will retry on next KeepAlive.");
                return;
            }

            try
            {
                // Prevent duplicate reconnect attempts
                if (Interlocked.CompareExchange(ref _isReconnecting, 0, 0) == 1)
                {
                    return;
                }

                var session = _session;
                if (session == null || !ReferenceEquals(sender, session))
                {
                    return;
                }

                // Check if SessionReconnectHandler is ready to begin reconnect
                if (_reconnectHandler.State != SessionReconnectHandler.ReconnectState.Ready)
                {
                    _logger.LogWarning("SessionReconnectHandler not ready. Current state: {State}", _reconnectHandler.State);
                    return;
                }

                _logger.LogInformation("Server connection lost. Beginning reconnect with exponential backoff...");

                // BeginReconnect returns the new state and triggers automatic reconnection
                // with exponential backoff (5s, 10s, 20s, 40s, 60s max)
                var newState = _reconnectHandler.BeginReconnect(
                    session,
                    5000, // Initial reconnect period
                    OnReconnectComplete);

                if (newState == SessionReconnectHandler.ReconnectState.Triggered ||
                    newState == SessionReconnectHandler.ReconnectState.Reconnecting)
                {
                    Interlocked.Exchange(ref _isReconnecting, 1);
                    e.CancelKeepAlive = true; // Stop keep-alive during reconnect

                    _logger.LogInformation("Reconnect handler initiated successfully.");
                }
                else
                {
                    _logger.LogError("Failed to begin reconnect. Handler state: {State}", newState);
                }
            }
            finally
            {
                _sessionSemaphore.Release();
            }
        }
    }

    /// <summary>
    /// Callback invoked by SessionReconnectHandler when reconnection completes.
    /// This is a synchronous callback from OPC UA library, so we queue async work.
    /// </summary>
    private void OnReconnectComplete(object? sender, EventArgs e)
    {
        // Queue the async work with full exception handling
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleReconnectCompleteAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in reconnect completion handler");
            }
        });
    }

    private async Task HandleReconnectCompleteAsync()
    {
        // Check if disposed
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
        {
            return;
        }

        await _sessionSemaphore.WaitAsync();
        try
        {
            var reconnectedSession = _reconnectHandler.Session;

            if (reconnectedSession == null)
            {
                _logger.LogError("Reconnect completed but received null session. Connection lost permanently.");
                Interlocked.Exchange(ref _isReconnecting, 0);
                SessionChanged?.Invoke(this, new SessionChangedEventArgs(null, false));
                return;
            }

            var isNewSession = !ReferenceEquals(_session, reconnectedSession);

            if (isNewSession)
            {
                _logger.LogInformation("Reconnect created new session. Subscriptions have been transferred by OPC UA stack.");

                var oldSession = _session;
                _session = reconnectedSession as Session;

                // Clean up old session properly
                if (oldSession != null && !ReferenceEquals(oldSession, _session))
                {
                    oldSession.KeepAlive -= OnKeepAlive;

                    // Dispose old session asynchronously with timeout
                    using var disposeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await DisposeSessionSafelyAsync(oldSession, disposeCts.Token);
                }

                // Setup KeepAlive for new session (only if it's a Session, not just ISession)
                if (_session != null)
                {
                    // Defensive - prevent duplicate registration
                    _session.KeepAlive -= OnKeepAlive;
                    _session.KeepAlive += OnKeepAlive;
                }

                SessionChanged?.Invoke(this, new SessionChangedEventArgs(_session, isNewSession));
            }
            else
            {
                _logger.LogInformation("Reconnect preserved existing session. Subscriptions maintained without transfer.");
            }

            Interlocked.Exchange(ref _isReconnecting, 0);
            _logger.LogInformation("Session reconnect completed successfully. New session: {IsNew}", isNewSession);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling reconnect completion");
            Interlocked.Exchange(ref _isReconnecting, 0);
        }
        finally
        {
            _sessionSemaphore.Release();
            ReconnectionCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Executes an action with the current session in a thread-safe manner.
    /// </summary>
    public async Task<T?> ExecuteWithSessionAsync<T>(
        Func<Session, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        await _sessionSemaphore.WaitAsync(cancellationToken);
        try
        {
            var session = _session;
            if (session == null)
            {
                return default;
            }

            return await action(session);
        }
        finally
        {
            _sessionSemaphore.Release();
        }
    }

    /// <summary>
    /// Closes the current session.
    /// </summary>
    public async Task CloseSessionAsync()
    {
        await _sessionSemaphore.WaitAsync();
        try
        {
            var session = _session;
            if (session == null)
            {
                return;
            }

            // Remove KeepAlive event handler before closing
            session.KeepAlive -= OnKeepAlive;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await session.CloseAsync(cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while closing session.");
            }
            finally
            {
                session.Dispose();
                _session = null;
                Interlocked.Exchange(ref _isReconnecting, 0);
            }
        }
        finally
        {
            _sessionSemaphore.Release();
        }
    }

    private async Task DisposeSessionSafelyAsync(Session session, CancellationToken cancellationToken)
    {
        try
        {
            await session.CloseAsync(cancellationToken);
            session.Dispose();
            _logger.LogDebug("Old session disposed after reconnect");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing old session");
        }
    }

    public void Dispose()
    {
        // Set disposed flag first to prevent new operations
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // Already disposed
        }

        // Acquire semaphore to ensure no operations are in progress
        _sessionSemaphore.Wait();
        try
        {
            var session = _session;
            if (session != null)
            {
                session.KeepAlive -= OnKeepAlive;
                session.Dispose();
                _session = null;
            }
        }
        finally
        {
            _sessionSemaphore.Release();
        }

        _reconnectHandler?.Dispose();
        _sessionSemaphore?.Dispose();
    }
}

/// <summary>
/// Event args for session change events.
/// </summary>
internal class SessionChangedEventArgs : EventArgs
{
    public Session? Session { get; }
    public bool IsNewSession { get; }

    public SessionChangedEventArgs(Session? session, bool isNewSession)
    {
        Session = session;
        IsNewSession = isNewSession;
    }
}