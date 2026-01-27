using Namotion.Interceptor.OpcUa.Mapping;
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

/// <summary>
/// Configures OPC UA node mapping for a property or class.
/// When applied to a class, provides default configuration for all properties of that type.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)]
public class OpcUaNodeAttribute : PathAttribute
{
    public OpcUaNodeAttribute(string browseName, string? browseNamespaceUri = null, string? connectorName = null)
        : base(connectorName ?? OpcUaConstants.DefaultConnectorName, browseName)
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
    /// Gets or sets the localized display name (if different from BrowseName).
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Gets or sets the human-readable description for the node.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets or sets the type definition (e.g., "FolderType", "AnalogItemType").
    /// </summary>
    public string? TypeDefinition { get; init; }

    /// <summary>
    /// Gets or sets the namespace URI for the type definition.
    /// </summary>
    public string? TypeDefinitionNamespace { get; init; }

    /// <summary>
    /// Gets or sets the NodeClass override.
    /// Default is Auto (auto-detect from C# type).
    /// Use Variable for classes representing VariableTypes (e.g., AnalogSignalVariableType).
    /// </summary>
    public OpcUaNodeClass NodeClass { get; init; } = OpcUaNodeClass.Auto;

    /// <summary>
    /// Gets or sets the DataType override (e.g., "Double", "NodeId").
    /// Default is null (infer from C# type).
    /// </summary>
    public string? DataType { get; init; }

    /// <summary>
    /// Gets or sets the namespace URI for the DataType.
    /// Used for custom data types from imported nodesets.
    /// </summary>
    public string? DataTypeNamespace { get; init; }

    /// <summary>
    /// Gets or sets the reference type from parent (e.g., "HasComponent", "HasProperty").
    /// </summary>
    public string? ReferenceType { get; init; }

    /// <summary>
    /// Gets or sets the namespace URI for the ReferenceType.
    /// Used for custom reference types from imported nodesets.
    /// </summary>
    public string? ReferenceTypeNamespace { get; init; }

    /// <summary>
    /// Gets or sets the sampling interval in milliseconds to be used in monitored item.
    /// Default is int.MinValue (not set), which uses the configuration default or OPC UA library default (-1 = server decides).
    /// Set to 0 for exception-based monitoring (immediate reporting on every change).
    /// </summary>
    /// <remarks>
    /// Uses int.MinValue as sentinel because C# attributes don't support nullable value types.
    /// Do not use int.MinValue as an actual sampling interval value.
    /// </remarks>
    public int SamplingInterval { get; init; } = int.MinValue;

    /// <summary>
    /// Gets or sets the queue size to be used in monitored item.
    /// Default is uint.MaxValue (not set), which uses the configuration default or OPC UA library default (1).
    /// </summary>
    /// <remarks>
    /// Uses uint.MaxValue as sentinel because C# attributes don't support nullable value types.
    /// Do not use uint.MaxValue as an actual queue size value.
    /// </remarks>
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

    /// <summary>
    /// Server only: Gets or sets the modelling rule (Mandatory, Optional, etc.).
    /// Default is Unset (not specified).
    /// </summary>
    public ModellingRule ModellingRule { get; init; } = ModellingRule.Unset;

    /// <summary>
    /// Server only: Gets or sets the event notifier flags for objects that emit events.
    /// Default is 255 (not set - uses server default).
    /// Set to 0 for "no events", or use EventNotifiers flags (1=SubscribeToEvents, 4=HistoryRead, 8=HistoryWrite).
    /// </summary>
    /// <remarks>
    /// Uses 255 (byte.MaxValue) as sentinel because C# attributes don't support nullable value types.
    /// Do not use 255 as an actual event notifier value.
    /// </remarks>
    public byte EventNotifier { get; init; } = byte.MaxValue;
}
