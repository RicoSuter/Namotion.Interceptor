using Namotion.Interceptor.Sources.Paths.Attributes;

namespace Namotion.Interceptor.OpcUa.Attributes;

public class OpcUaNodeAttribute : SourcePathAttribute
{
    public OpcUaNodeAttribute(string browseName, string? browseNamespaceUri, string? sourceName = null)
        : base(sourceName ?? "opc", browseName)
    {
        BrowseName = browseName;
        BrowseNamespaceUri = browseNamespaceUri;
    }

    /// <summary>
    /// Gets the BrowseName of the node to browse for (relative to the parent node).
    /// </summary>
    public string BrowseName { get; }

    /// <summary>
    /// Gets the namespace URI of the BrowseName (uses default namespace when null).
    /// </summary>
    public string? BrowseNamespaceUri { get; }

    /// <summary>
    /// Gets the node identifier to enforce an exact, global Node ID match when connecting.
    /// </summary>
    public string? NodeIdentifier { get; set; }

    /// <summary>
    /// Gets the node namespace URI (uses default namespace from client configuration when null).
    /// </summary>
    public string? NodeNamespaceUri { get; set; }
}
