using System.Reflection;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class OpcUaSubjectServer : StandardServer
{
    // Workaround for OPC UA SDK bug: TcpTransportListener.Dispose() doesn't null m_callback,
    // and channel Dispose() doesn't close sockets. Lingering sockets (held by SocketAsyncEngine)
    // retain channels which chain: Channel → Listener → m_callback → SessionEndpoint → Server.
    // No public API exists to break this chain — ChannelClosed() is protected, Socket is
    // protected internal. We null m_callback after disposal to allow GC of the server graph.
    // Upstream SDK issue: TcpTransportListener.Dispose() doesn't null m_callback and channel
    // Dispose() doesn't close sockets. Tracked for upstream contribution.
    private static readonly FieldInfo? TransportListenerCallbackField =
        typeof(Opc.Ua.Bindings.TcpTransportListener)
            .GetField("m_callback", BindingFlags.NonPublic | BindingFlags.Instance);

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
    /// Disposes all saved transport listeners, closing per-client channel
    /// sockets and timers. Must be called after shutdown to work around
    /// the SDK's StopAsync clearing TransportListeners before Dispose
    /// can process them (which causes a memory leak).
    /// </summary>
    public void DisposeTransportListeners()
    {
        if (_savedTransportListeners is null)
        {
            return;
        }

        if (TransportListenerCallbackField is null)
        {
            _logger.LogWarning(
                "TcpTransportListener.m_callback field not found. " +
                "The OPC UA SDK may have changed its internals — transport listener memory leak workaround is inactive.");
        }

        foreach (var listener in _savedTransportListeners)
        {
            try { (listener as IDisposable)?.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error disposing transport listener."); }

            try { TransportListenerCallbackField?.SetValue(listener, null); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error clearing transport listener callback."); }
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
            // Unsubscribe from the CertificateValidator.CertificateUpdate event.
            // The SDK subscribes in StandardServer.OnServerStarted but never unsubscribes.
            // Since the CertificateValidator is shared (from ApplicationConfiguration) and
            // outlives the server, the delegate retains the server instance. Combined with
            // lingering sockets that hold ChannelQuotas → CertificateValidator references,
            // this creates a GC root chain: Socket → Channel → ChannelQuotas →
            // CertificateValidator → CertificateUpdateEventHandler → Server.
            if (CertificateValidator is not null)
            {
                CertificateValidator.CertificateUpdate -= OnCertificateUpdateAsync;
            }

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
