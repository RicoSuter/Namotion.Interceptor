using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class OpcUaSubjectServer : StandardServer
{
    private readonly ILogger _logger;
    private readonly CustomNodeManagerFactory _nodeManagerFactory;

    private IServerInternal? _server;
    private SessionEventHandler? _sessionCreatedHandler;
    private SessionEventHandler? _sessionClosingHandler;
    private List<ITransportListener>? _savedTransportListeners;

    public OpcUaSubjectServer(IInterceptorSubject subject, OpcUaSubjectServerBackgroundService source, OpcUaServerConfiguration configuration, ILogger logger)
    {
        _logger = logger;
        _nodeManagerFactory = new CustomNodeManagerFactory(subject, source, configuration, logger);
        AddNodeManager(_nodeManagerFactory);
    }

    // TODO: Remove saved listener workaround when https://github.com/OPCFoundation/UA-.NETStandard/pull/3561 is released.
    // Workaround: ServerBase.StopAsync calls Close() then Clear() on the listener list.
    // TcpTransportListener.Close() only stops listening sockets — it does NOT call Dispose().
    // ServerBase.Dispose() later iterates TransportListeners to dispose them, but the list is
    // already empty. So TcpTransportListener.Dispose() never runs, leaking timers, channels,
    // and buffer managers. We save listener references before StopAsync clears the list,
    // then manually dispose them after shutdown.

    /// <summary>
    /// Closes all transport listeners to stop accepting new connections.
    /// Must be called before closing sessions during shutdown to prevent
    /// clients from reconnecting while the server is shutting down.
    /// Also saves references so they can be properly disposed later,
    /// since the SDK's StopAsync clears the TransportListeners list
    /// before Dispose can process them.
    /// </summary>
    public void CloseTransportListeners()
    {
        _savedTransportListeners ??= [.. TransportListeners];
        foreach (var listener in _savedTransportListeners)
        {
            try { listener.Close(); } catch (Exception ex) { _logger.LogDebug(ex, "Error closing transport listener."); }
        }
    }

    /// <summary>
    /// Disposes all saved transport listeners. Must be called after shutdown
    /// because the SDK's StopAsync clears the TransportListeners list before
    /// Dispose can process them, causing TcpTransportListener.Dispose() to
    /// never run (leaking timers, channels, and buffer managers).
    /// </summary>
    public void DisposeTransportListeners()
    {
        if (_savedTransportListeners is null)
        {
            return;
        }

        foreach (var listener in _savedTransportListeners)
        {
            try { (listener as IDisposable)?.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error disposing transport listener."); }
        }

        _savedTransportListeners = null;
    }

    /// <summary>
    /// Gets the node manager's lock object for thread-safe node updates.
    /// This is the same lock used by the SDK for Read/Write operations.
    /// </summary>
    internal object? NodeManagerLock => _nodeManagerFactory.NodeManager?.Lock;

    public void ClearPropertyData()
    {
        _nodeManagerFactory.NodeManager?.ClearPropertyData();
    }

    public void RemoveSubjectNodes(IInterceptorSubject subject)
    {
        _nodeManagerFactory.NodeManager?.RemoveSubjectNodes(subject);
    }

    protected override void OnServerStarted(IServerInternal server)
    {
        // Unsubscribe any existing handlers to prevent accumulation on server restart
        if (_server is not null && _sessionCreatedHandler is not null)
        {
            _server.SessionManager.SessionCreated -= _sessionCreatedHandler;
        }
        if (_server is not null && _sessionClosingHandler is not null)
        {
            _server.SessionManager.SessionClosing -= _sessionClosingHandler;
        }

        _server = server;

        _sessionCreatedHandler = (session, _) =>
        {
            _logger.LogInformation("OPC UA session {SessionId} with user {UserIdentity} created.", session.Id, session.Identity.DisplayName);
        };

        _sessionClosingHandler = (session, _) =>
        {
            _logger.LogInformation("OPC UA session {SessionId} with user {UserIdentity} closing.", session.Id, session.Identity.DisplayName);
        };

        server.SessionManager.SessionCreated += _sessionCreatedHandler;
        server.SessionManager.SessionClosing += _sessionClosingHandler;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_server is not null)
            {
                if (_sessionCreatedHandler is not null)
                {
                    _server.SessionManager.SessionCreated -= _sessionCreatedHandler;
                }

                if (_sessionClosingHandler is not null)
                {
                    _server.SessionManager.SessionClosing -= _sessionClosingHandler;
                }

                _server = null;
            }
        }

        base.Dispose(disposing);
    }
}
