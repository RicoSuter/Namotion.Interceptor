using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua.Server;
using Opc.Ua;

namespace Namotion.Proxy.OpcUa.Server;

internal class CustomNodeManagerFactory<TSubject> : INodeManagerFactory
    where TSubject : IInterceptorSubject
{
    private readonly TSubject _subject;
    private readonly OpcUaSubjectServerSource<TSubject> _source;
    private readonly string? _rootName;

    public StringCollection NamespacesUris => new StringCollection([
        "https://foobar/",
        "http://opcfoundation.org/UA/",
        "http://opcfoundation.org/UA/DI/",
        "http://opcfoundation.org/UA/PADIM",
        "http://opcfoundation.org/UA/Machinery/",
        "http://opcfoundation.org/UA/Machinery/ProcessValues"
    ]);

    public CustomNodeManagerFactory(TSubject subject, OpcUaSubjectServerSource<TSubject> source, string? rootName)
    {
        _subject = subject;
        _source = source;
        _rootName = rootName;
    }

    public INodeManager Create(IServerInternal server, ApplicationConfiguration configuration)
    {
        return new CustomNodeManager<TSubject>(_subject, _source, server, configuration, _rootName);
    }
}
