using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class CustomNodeManagerFactory : INodeManagerFactory
{
    private readonly IInterceptorSubject _subject;
    private readonly OpcUaSubjectServerSource _source;
    private readonly OpcUaServerConfiguration _configuration;

    public StringCollection NamespacesUris => new(_configuration.GetNamespaceUris());

    public CustomNodeManager? NodeManager { get; private set; }

    public CustomNodeManagerFactory(IInterceptorSubject subject, OpcUaSubjectServerSource source, OpcUaServerConfiguration configuration)
    {
        _subject = subject;
        _source = source;
        _configuration = configuration;
    }

    public INodeManager Create(IServerInternal server, ApplicationConfiguration applicationConfiguration)
    {
        NodeManager = new CustomNodeManager(_subject, _source, server, applicationConfiguration, _configuration);
        return NodeManager;
    }
}
