using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class CustomNodeManagerFactory : INodeManagerFactory
{
    private readonly IInterceptorSubject _subject;
    private readonly OpcUaSubjectServerSource _source;
    private readonly string? _rootName;

    public StringCollection NamespacesUris => new StringCollection([
        "https://foobar/",
        "http://opcfoundation.org/UA/",
        "http://opcfoundation.org/UA/DI/",
        "http://opcfoundation.org/UA/PADIM",
        "http://opcfoundation.org/UA/Machinery/",
        "http://opcfoundation.org/UA/Machinery/ProcessValues"
    ]);

    public CustomNodeManagerFactory(IInterceptorSubject subject, OpcUaSubjectServerSource source, string? rootName)
    {
        _subject = subject;
        _source = source;
        _rootName = rootName;
    }

    public INodeManager Create(IServerInternal server, ApplicationConfiguration configuration)
    {
        return new CustomNodeManager(_subject, _source, server, configuration, _rootName);
    }
}
