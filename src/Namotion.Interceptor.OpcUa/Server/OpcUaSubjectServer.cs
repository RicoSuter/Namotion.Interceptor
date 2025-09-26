using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class OpcUaSubjectServer : StandardServer
{
    public OpcUaSubjectServer(IInterceptorSubject subject, OpcUaSubjectServerSource source, OpcUaServerConfiguration configuration)
    {
        AddNodeManager(new CustomNodeManagerFactory(subject, source, configuration));
    }
}
