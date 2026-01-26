using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// OPC UA node and reference configuration. All fields are nullable to support partial
/// configuration and merge semantics.
/// </summary>
public record OpcUaNodeConfiguration
{
    // Node identification (shared)
    /// <summary>Browse name for the node.</summary>
    public string? BrowseName { get; init; }

    /// <summary>Namespace URI for the browse name.</summary>
    public string? BrowseNamespaceUri { get; init; }

    /// <summary>Explicit node identifier. Property-level only.</summary>
    public string? NodeIdentifier { get; init; }

    /// <summary>Namespace URI for the node identifier.</summary>
    public string? NodeNamespaceUri { get; init; }

    /// <summary>Localized display name (if different from BrowseName).</summary>
    public string? DisplayName { get; init; }

    /// <summary>Human-readable description.</summary>
    public string? Description { get; init; }

    // Type definition (shared)
    /// <summary>Type definition (e.g., "FolderType", "AnalogItemType").</summary>
    public string? TypeDefinition { get; init; }

    /// <summary>Namespace for type definition.</summary>
    public string? TypeDefinitionNamespace { get; init; }

    /// <summary>NodeClass override (null = auto-detect from C# type).</summary>
    public OpcUaNodeClass? NodeClass { get; init; }

    /// <summary>DataType override (null = infer from C# type). Examples: "Double", "NodeId".</summary>
    public string? DataType { get; init; }

    /// <summary>True if this property holds the main value for a VariableNode class.</summary>
    public bool? IsValue { get; init; }

    // Reference configuration
    /// <summary>Reference type from parent (e.g., "HasComponent", "HasProperty"). Default: "HasProperty".</summary>
    public string? ReferenceType { get; init; }

    /// <summary>Reference type for collection/dictionary items.</summary>
    public string? ItemReferenceType { get; init; }

    // Monitoring configuration (client only)
    /// <summary>Client only: Sampling interval in milliseconds.</summary>
    public int? SamplingInterval { get; init; }

    /// <summary>Client only: Queue size for monitored items.</summary>
    public uint? QueueSize { get; init; }

    /// <summary>Client only: Whether to discard oldest values when queue is full.</summary>
    public bool? DiscardOldest { get; init; }

    /// <summary>Client only: Trigger for data change notifications.</summary>
    public DataChangeTrigger? DataChangeTrigger { get; init; }

    /// <summary>Client only: Deadband type for filtering value changes.</summary>
    public DeadbandType? DeadbandType { get; init; }

    /// <summary>Client only: Deadband value for filtering.</summary>
    public double? DeadbandValue { get; init; }

    // Server configuration
    /// <summary>Server only: Modelling rule (Mandatory, Optional, etc.).</summary>
    public ModellingRule? ModellingRule { get; init; }

    /// <summary>Server only: Event notifier flags for objects that emit events.</summary>
    public byte? EventNotifier { get; init; }

    /// <summary>Server only: Additional non-hierarchical references (HasInterface, etc.).</summary>
    public IReadOnlyList<OpcUaAdditionalReference>? AdditionalReferences { get; init; }

    /// <summary>
    /// Returns a new configuration using <paramref name="other"/> as fallback for null fields.
    /// This configuration takes priority; null fields are filled from the fallback.
    /// </summary>
    /// <param name="other">Fallback configuration to use when this has null fields.</param>
    public OpcUaNodeConfiguration WithFallback(OpcUaNodeConfiguration? other)
    {
        if (other is null) return this;

        return new OpcUaNodeConfiguration
        {
            BrowseName = BrowseName ?? other.BrowseName,
            BrowseNamespaceUri = BrowseNamespaceUri ?? other.BrowseNamespaceUri,
            NodeIdentifier = NodeIdentifier ?? other.NodeIdentifier,
            NodeNamespaceUri = NodeNamespaceUri ?? other.NodeNamespaceUri,
            DisplayName = DisplayName ?? other.DisplayName,
            Description = Description ?? other.Description,
            TypeDefinition = TypeDefinition ?? other.TypeDefinition,
            TypeDefinitionNamespace = TypeDefinitionNamespace ?? other.TypeDefinitionNamespace,
            NodeClass = NodeClass ?? other.NodeClass,
            DataType = DataType ?? other.DataType,
            IsValue = IsValue ?? other.IsValue,
            ReferenceType = ReferenceType ?? other.ReferenceType,
            ItemReferenceType = ItemReferenceType ?? other.ItemReferenceType,
            SamplingInterval = SamplingInterval ?? other.SamplingInterval,
            QueueSize = QueueSize ?? other.QueueSize,
            DiscardOldest = DiscardOldest ?? other.DiscardOldest,
            DataChangeTrigger = DataChangeTrigger ?? other.DataChangeTrigger,
            DeadbandType = DeadbandType ?? other.DeadbandType,
            DeadbandValue = DeadbandValue ?? other.DeadbandValue,
            ModellingRule = ModellingRule ?? other.ModellingRule,
            EventNotifier = EventNotifier ?? other.EventNotifier,
            AdditionalReferences = MergeAdditionalReferences(AdditionalReferences, other.AdditionalReferences)
        };
    }

    /// <summary>
    /// Merges two AdditionalReferences lists. When both are non-null, combines them.
    /// </summary>
    private static IReadOnlyList<OpcUaAdditionalReference>? MergeAdditionalReferences(
        IReadOnlyList<OpcUaAdditionalReference>? primary,
        IReadOnlyList<OpcUaAdditionalReference>? fallback)
    {
        if (primary is null)
            return fallback;
        if (fallback is null)
            return primary;

        // Both are non-null, merge them
        return [.. primary, .. fallback];
    }
}
