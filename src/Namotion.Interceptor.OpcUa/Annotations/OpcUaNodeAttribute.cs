using Namotion.Interceptor.Sources.Paths.Attributes;

namespace Namotion.Interceptor.OpcUa.Annotations;

public class OpcUaNodeAttribute : SourcePathAttribute
{
    public OpcUaNodeAttribute(string browseName, string? browseNamespaceUri, string? sourceName = null)
        : base(sourceName ?? "opc", browseName)
    {
        BrowseName = browseName;
        BrowseNamespaceUri = browseNamespaceUri;
    }

    public string BrowseName { get; }

    /// <summary>
    /// Gets the namespace URI of the BrowseName (uses default namespace when null).
    /// </summary>
    public string? BrowseNamespaceUri { get; }

    public string? NodeIdentifier { get; set; }

    public string? NodeNamespaceUri { get; set; }
}
