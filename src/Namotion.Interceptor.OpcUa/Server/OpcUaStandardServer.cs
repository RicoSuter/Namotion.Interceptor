using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class OpcUaStandardServer : StandardServer
{
    private readonly ILogger _logger;
    private readonly CustomNodeManagerFactory _nodeManagerFactory;

    private ISessionManager? _sessionManager;
    private SessionEventHandler? _sessionCreatedHandler;
    private SessionEventHandler? _sessionClosingHandler;
    private int _activeSessionCount;

    public OpcUaStandardServer(IInterceptorSubject subject, OpcUaSubjectServer source, OpcUaServerConfiguration configuration, ILogger logger)
    {
        _logger = logger;
        _nodeManagerFactory = new CustomNodeManagerFactory(subject, source, configuration, logger);
        AddNodeManager(_nodeManagerFactory);
    }

    /// <inheritdoc />
    public override void ScheduleIncomingRequest(IEndpointIncomingRequest request, CancellationToken cancellationToken = default)
    {
        // Works around an SDK defect: the request queue's disposal can complete a request that a worker
        // still processes, and the endpoint completes it again when scheduling throws after the enqueue.
        // A second completion lands on a pooled response slot that already belongs to a later request, so
        // responses can be delivered to the wrong request. Reported to the OPC Foundation; remove this
        // once the SDK completes each request only once.
        var guardedRequest = new SingleCompletionIncomingRequest(request);
        try
        {
            base.ScheduleIncomingRequest(guardedRequest, cancellationToken);
        }
        catch (Exception exception)
        {
            // Completed here and not rethrown, so the endpoint's own catch never adds a completion to one
            // the queue may already have made.
            _logger.LogDebug(exception, "Failed to schedule an incoming OPC UA request.");
            guardedRequest.OperationCompleted(null, exception is ObjectDisposedException
                ? new ServiceResult(StatusCodes.BadServerHalted)
                : new ServiceResult(exception));
        }
    }

    /// <summary>
    /// Closes all transport listeners to stop accepting new connections.
    /// Must be called before closing sessions during shutdown to prevent
    /// clients from reconnecting while the server is shutting down.
    /// </summary>
    /// <remarks>
    /// The SDK's StopAsync disposes the listeners itself (close, dispose, then clear),
    /// so no manual disposal is needed here. See OPCFoundation/UA-.NETStandard#3561.
    /// </remarks>
    public void CloseTransportListeners()
    {
        foreach (var listener in TransportListeners)
        {
            try { listener.Close(); } catch (Exception ex) { _logger.LogDebug(ex, "Error closing transport listener."); }
        }
    }

    /// <summary>
    /// Gets the node manager's lock object for thread-safe node updates.
    /// This is the same lock used by the SDK for Read/Write operations.
    /// </summary>
    internal object? NodeManagerLock => _nodeManagerFactory.NodeManager?.Lock;

    // Clamped because a session-closing event can race the counter reset and briefly drive the
    // count negative, and a negative count is a worse report than zero.
    internal int ActiveSessionCount => Math.Max(0, Volatile.Read(ref _activeSessionCount));

    public void ClearPropertyData()
    {
        _nodeManagerFactory.NodeManager?.ClearPropertyData();
    }

    public void RemoveSubjectNodes(IInterceptorSubject subject)
    {
        _nodeManagerFactory.NodeManager?.RemoveSubjectNodes(subject);
    }

    protected override ISessionManager CreateSessionManager(
        IServerInternal server,
        ApplicationConfiguration configuration)
    {
        var sessionManager = base.CreateSessionManager(server, configuration);
        _sessionManager = sessionManager;
        Interlocked.Exchange(ref _activeSessionCount, 0);

        _sessionCreatedHandler = (session, _) =>
        {
            var count = Interlocked.Increment(ref _activeSessionCount);
            _logger.LogInformation(
                "OPC UA session {SessionId} with user {UserIdentity} created. Active sessions: {Count}.",
                session.Id, session.Identity.DisplayName, count);
        };

        _sessionClosingHandler = (session, _) =>
        {
            var count = Interlocked.Decrement(ref _activeSessionCount);
            _logger.LogInformation(
                "OPC UA session {SessionId} with user {UserIdentity} closing. Active sessions: {Count}.",
                session.Id, session.Identity.DisplayName, count);
        };

        sessionManager.SessionCreated += _sessionCreatedHandler;
        sessionManager.SessionClosing += _sessionClosingHandler;
        return sessionManager;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var sessionManager = _sessionManager;
            if (sessionManager is not null)
            {
                if (_sessionCreatedHandler is not null)
                {
                    sessionManager.SessionCreated -= _sessionCreatedHandler;
                }

                if (_sessionClosingHandler is not null)
                {
                    sessionManager.SessionClosing -= _sessionClosingHandler;
                }

                _sessionManager = null;
                _sessionCreatedHandler = null;
                _sessionClosingHandler = null;
                Interlocked.Exchange(ref _activeSessionCount, 0);
            }
        }

        base.Dispose(disposing);
    }
}
