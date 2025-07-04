using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class OpcUaSubjectServer : StandardServer
{
    public OpcUaSubjectServer(IInterceptorSubject subject, OpcUaSubjectServerSource source, string? rootName)
    {
        AddNodeManager(new CustomNodeManagerFactory(subject, source, rootName));
    }
}
