using Namotion.Interceptor;
using Namotion.Proxy.Registry.Abstractions;
using Opc.Ua.Server;
using Opc.Ua;

namespace Namotion.Proxy.OpcUa.Server;

internal class CustomNodeManagerFactory<TProxy> : INodeManagerFactory
    where TProxy : IInterceptorSubject
{
    private readonly TProxy _proxy;
    private readonly OpcUaServerTrackableSource<TProxy> _source;
    private readonly string? _rootName;
    private readonly IProxyRegistry _registry;

    public StringCollection NamespacesUris => new StringCollection([
        "https://foobar/",
        "http://opcfoundation.org/UA/",
        "http://opcfoundation.org/UA/DI/",
        "http://opcfoundation.org/UA/PADIM",
        "http://opcfoundation.org/UA/Machinery/",
        "http://opcfoundation.org/UA/Machinery/ProcessValues"
    ]);

    public CustomNodeManagerFactory(TProxy proxy, OpcUaServerTrackableSource<TProxy> source, string? rootName, IProxyRegistry registry)
    {
        _proxy = proxy;
        _source = source;
        _rootName = rootName;
        _registry = registry;
    }

    public INodeManager Create(IServerInternal server, ApplicationConfiguration configuration)
    {
        return new CustomNodeManager<TProxy>(_proxy, _source, server, configuration, _rootName, _registry);
    }
}
