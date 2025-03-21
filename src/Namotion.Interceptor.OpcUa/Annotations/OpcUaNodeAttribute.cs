using Namotion.Interceptor.Sources.Paths.Attributes;

namespace Namotion.Interceptor.OpcUa.Annotations;

public class OpcUaNodeAttribute : SourcePathAttribute, IOpcUaBrowseNameProvider
{
    public OpcUaNodeAttribute(string browseName, string browseNamespace, string? sourceName = null, string? path = null)
        : base(sourceName ?? "opc", path ?? browseName)
    {
        BrowseName = browseName;
        BrowseNamespace = browseNamespace;
    }

    public string BrowseName { get; }

    public string BrowseNamespace { get; }
}


public class OpcUaVariableAttribute : SourceNameAttribute, IOpcUaBrowseNameProvider
{
    public OpcUaVariableAttribute(string browseName, string browseNamespace, string? sourceName = null, string? path = null)
        : base(sourceName ?? "opc", path ?? browseName)
    {
        BrowseName = browseName;
        BrowseNamespace = browseNamespace;
    }

    public string BrowseName { get; }

    public string BrowseNamespace { get; }
}

public interface IOpcUaBrowseNameProvider
{
    string BrowseName { get; }

    string BrowseNamespace { get; }
}
