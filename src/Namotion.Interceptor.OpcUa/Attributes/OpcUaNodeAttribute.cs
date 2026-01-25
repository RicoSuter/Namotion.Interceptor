using Namotion.Interceptor.Registry.Attributes;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Attributes;

/// <summary>
/// Tri-state mode for DiscardOldest setting, used because C# attributes don't support nullable bool.
/// </summary>
public enum DiscardOldestMode
{
    /// <summary>Not set - uses configuration default or OPC UA library default (true).</summary>
    Unset = -1,

    /// <summary>Do not discard oldest - fail when queue is full.</summary>
    False = 0,

    /// <summary>Discard oldest value when queue is full.</summary>
    True = 1
}

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
    /// Gets or sets the sampling interval in milliseconds to be used in monitored item.
    /// Default is int.MinValue (not set), which uses the configuration default or OPC UA library default (-1 = server decides).
    /// Set to 0 for exception-based monitoring (immediate reporting on every change).
    /// Note: Uses int.MinValue as sentinel because C# attributes don't support nullable value types.
    /// </summary>
    public int SamplingInterval { get; init; } = int.MinValue;

    /// <summary>
    /// Gets or sets the queue size to be used in monitored item.
    /// Default is uint.MaxValue (not set), which uses the configuration default or OPC UA library default (1).
    /// Note: Uses uint.MaxValue as sentinel because C# attributes don't support nullable value types.
    /// </summary>
    public uint QueueSize { get; init; } = uint.MaxValue;

    /// <summary>
    /// Gets or sets whether the server should discard the oldest value in the queue when the queue is full.
    /// Default is DiscardOldestMode.Unset (not set), which uses the configuration default or OPC UA library default (true).
    /// Note: Uses a tri-state enum because C# attributes don't support nullable value types for bool.
    /// </summary>
    public DiscardOldestMode DiscardOldest { get; init; } = DiscardOldestMode.Unset;

    /// <summary>
    /// Gets or sets the data change trigger that determines which value changes generate notifications.
    /// Default is -1 (not set), which uses the configuration default or OPC UA library default (StatusValue).
    /// Note: Uses -1 as sentinel because C# attributes don't support nullable value types.
    /// </summary>
    public DataChangeTrigger DataChangeTrigger { get; init; } = (DataChangeTrigger)(-1);

    /// <summary>
    /// Gets or sets the deadband type for filtering small value changes.
    /// Default is -1 (not set), which uses the configuration default or OPC UA library default (None).
    /// Use Absolute or Percent for analog values to filter noise.
    /// Note: Uses -1 as sentinel because C# attributes don't support nullable value types.
    /// </summary>
    public DeadbandType DeadbandType { get; init; } = (DeadbandType)(-1);

    /// <summary>
    /// Gets or sets the deadband value threshold.
    /// Default is NaN (not set), which uses the configuration default or OPC UA library default (0.0).
    /// The interpretation depends on DeadbandType: absolute units for Absolute, percentage for Percent.
    /// Note: Uses NaN as sentinel because C# attributes don't support nullable value types.
    /// </summary>
    public double DeadbandValue { get; init; } = double.NaN;
}
