using Microsoft.Extensions.Logging;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class OpcUaSubjectServer : StandardServer
{
    private readonly ILogger _logger;
    private readonly CustomNodeManagerFactory _nodeManagerFactory;

    private IServerInternal? _server;
    private SessionEventHandler? _sessionCreatedHandler;
    private SessionEventHandler? _sessionClosingHandler;

    public OpcUaSubjectServer(IInterceptorSubject subject, OpcUaSubjectServerBackgroundService source, OpcUaServerConfiguration configuration, ILogger logger)
    {
        _logger = logger;
        _nodeManagerFactory = new CustomNodeManagerFactory(subject, source, configuration);
        AddNodeManager(_nodeManagerFactory);
    }

    public void ClearPropertyData()
    {
        _nodeManagerFactory.NodeManager?.ClearPropertyData();
    }

    public void RemoveSubjectNodes(IInterceptorSubject subject)
    {
        _nodeManagerFactory.NodeManager?.RemoveSubjectNodes(subject);
    }

    public CustomNodeManager? GetNodeManager() => _nodeManagerFactory.NodeManager;

    public IServerInternal? GetServerInternal() => _server;

    protected override void OnServerStarted(IServerInternal server)
    {
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
        if (disposing && _server is not null)
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

        base.Dispose(disposing);
    }
}
