using Namotion.Interceptor.Registry.Attributes;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Attributes;

public class OpcUaNodeAttribute : PathAttribute
{
    public OpcUaNodeAttribute(string browseName, string? browseNamespaceUri, string? connectorName = null)
        : base(connectorName ?? "opc", browseName)
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
    public string? NodeIdentifier { get; init; }

    /// <summary>
    /// Gets the node namespace URI (uses default namespace from client configuration when null).
    /// </summary>
    public string? NodeNamespaceUri { get; init; }

    /// <summary>
    /// Gets or sets the sampling interval in milliseconds to be used in monitored item (default: null, use default).
    /// </summary>
    public int? SamplingInterval { get; init; }

    /// <summary>
    /// Gets or sets the queue size to be used in monitored item (default: null, use default).
    /// </summary>
    public uint? QueueSize { get; init; }

    /// <summary>
    /// Gets or sets whether the server should discard the oldest value in the queue when the queue is full (default: null, use default).
    /// </summary>
    public bool? DiscardOldest { get; init; }

    /// <summary>
    /// Gets or sets the data change trigger that determines which value changes generate notifications.
    /// When null (default), uses the configuration default or OPC UA library default (StatusValue).
    /// </summary>
    public DataChangeTrigger? DataChangeTrigger { get; init; }

    /// <summary>
    /// Gets or sets the deadband type for filtering small value changes.
    /// When null (default), uses the configuration default or OPC UA library default (None).
    /// Use Absolute or Percent for analog values to filter noise.
    /// </summary>
    public DeadbandType? DeadbandType { get; init; }

    /// <summary>
    /// Gets or sets the deadband value threshold.
    /// When null (default), uses the configuration default or OPC UA library default (0.0).
    /// The interpretation depends on DeadbandType: absolute units for Absolute, percentage for Percent.
    /// </summary>
    public double? DeadbandValue { get; init; }
}
