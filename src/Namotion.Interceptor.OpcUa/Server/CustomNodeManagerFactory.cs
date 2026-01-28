using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class CustomNodeManagerFactory : INodeManagerFactory
{
    private readonly IInterceptorSubject _subject;
    private readonly OpcUaSubjectServerBackgroundService _source;
    private readonly OpcUaServerConfiguration _configuration;
    private readonly ILogger _logger;

    public StringCollection NamespacesUris => new(_configuration.GetNamespaceUris());

    public CustomNodeManager? NodeManager { get; private set; }

    public CustomNodeManagerFactory(
        IInterceptorSubject subject,
        OpcUaSubjectServerBackgroundService source,
        OpcUaServerConfiguration configuration,
        ILogger logger)
    {
        _subject = subject;
        _source = source;
        _configuration = configuration;
        _logger = logger;
    }

    public INodeManager Create(IServerInternal server, ApplicationConfiguration applicationConfiguration)
    {
        NodeManager = new CustomNodeManager(_subject, _source, server, applicationConfiguration, _configuration, _logger);
        return NodeManager;
    }
}
