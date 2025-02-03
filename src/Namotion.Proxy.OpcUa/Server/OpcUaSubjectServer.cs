using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua.Server;

namespace Namotion.Proxy.OpcUa.Server;

internal class OpcUaSubjectServer<TSubject> : StandardServer
    where TSubject : IInterceptorSubject
{
    public OpcUaSubjectServer(TSubject subject, OpcUaSubjectServerSource<TSubject> source, string? rootName)
    {
        AddNodeManager(new CustomNodeManagerFactory<TSubject>(subject, source, rootName));
    }
}
