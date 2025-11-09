using Microsoft.Extensions.Logging;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class OpcUaSubjectServer : StandardServer
{
    private readonly ILogger _logger;

    public OpcUaSubjectServer(IInterceptorSubject subject, OpcUaSubjectServerSource source, OpcUaServerConfiguration configuration, ILogger logger)
    {
        _logger = logger;
        AddNodeManager(new CustomNodeManagerFactory(subject, source, configuration));
    }

    protected override void OnServerStarted(IServerInternal server)
    {
        server.SessionManager.SessionCreated += (s, _) =>
        {
            _logger.LogInformation("OPC UA session {SessionId} created.", s.Id);
        };

        server.SessionManager.SessionClosing += (s, _) =>
        {
            _logger.LogInformation("OPC UA session {SessionId} closing.", s.Id);
        };
    }
}
