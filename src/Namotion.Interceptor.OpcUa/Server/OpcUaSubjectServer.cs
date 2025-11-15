using Microsoft.Extensions.Logging;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class OpcUaSubjectServer : StandardServer
{
    private readonly ILogger _logger;
    private readonly CustomNodeManagerFactory _nodeManagerFactory;

    public OpcUaSubjectServer(IInterceptorSubject subject, OpcUaSubjectServerSource source, OpcUaServerConfiguration configuration, ILogger logger)
    {
        _logger = logger;
        _nodeManagerFactory = new CustomNodeManagerFactory(subject, source, configuration);
        AddNodeManager(_nodeManagerFactory);
    }

    public void ClearPropertyData()
    {
        _nodeManagerFactory.NodeManager?.ClearPropertyData();
    }

    protected override void OnServerStarted(IServerInternal server)
    {
        server.SessionManager.SessionCreated += (s, _) =>
        {
            _logger.LogInformation("OPC UA session {SessionId} with user {UserIdentity} created.", s.Id, s.Identity.DisplayName);
        };

        server.SessionManager.SessionClosing += (s, _) =>
        {
            _logger.LogInformation("OPC UA session {SessionId} with user {UserIdentity} closing.", s.Id, s.Identity.DisplayName);
        };
    }
}
