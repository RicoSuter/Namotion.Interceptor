using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class CustomNodeManagerFactory : INodeManagerFactory
{
    private readonly IInterceptorSubject _subject;
    private readonly OpcUaServerConnector _connector;
    private readonly OpcUaServerConfiguration _configuration;

    public StringCollection NamespacesUris => new(_configuration.GetNamespaceUris());

    public CustomNodeManager? NodeManager { get; private set; }

    public CustomNodeManagerFactory(IInterceptorSubject subject, OpcUaServerConnector connector, OpcUaServerConfiguration configuration)
    {
        _subject = subject;
        _connector = connector;
        _configuration = configuration;
    }

    public INodeManager Create(IServerInternal server, ApplicationConfiguration applicationConfiguration)
    {
        NodeManager = new CustomNodeManager(_subject, _connector, server, applicationConfiguration, _configuration);
        return NodeManager;
    }
}
