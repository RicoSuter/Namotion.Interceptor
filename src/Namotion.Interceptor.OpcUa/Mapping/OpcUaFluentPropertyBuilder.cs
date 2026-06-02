using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Per-member fluent configuration for OPC UA. <see cref="BrowseName"/> sets both the path segment (for
/// composition and reverse navigation) and the node BrowseName. Other setters configure node and
/// monitoring metadata, mirroring the <see cref="OpcUaPropertyMapping"/> fields.
/// </summary>
public sealed class OpcUaFluentPropertyBuilder
{
    private string? _segment;
    private OpcUaPropertyMapping _mapping = new();

    /// <summary>Sets the browse name for the node, and the path segment used for composition and reverse navigation.</summary>
    public OpcUaFluentPropertyBuilder BrowseName(string value)
    {
        _segment = value;
        _mapping = _mapping with { BrowseName = value };
        return this;
    }

    /// <summary>Sets the namespace URI for the browse name.</summary>
    public OpcUaFluentPropertyBuilder BrowseNamespaceUri(string value) { _mapping = _mapping with { BrowseNamespaceUri = value }; return this; }

    /// <summary>Sets an explicit node identifier (property-level only).</summary>
    public OpcUaFluentPropertyBuilder NodeIdentifier(string value) { _mapping = _mapping with { NodeIdentifier = value }; return this; }

    /// <summary>Sets the namespace URI for the node identifier.</summary>
    public OpcUaFluentPropertyBuilder NodeNamespaceUri(string value) { _mapping = _mapping with { NodeNamespaceUri = value }; return this; }

    /// <summary>Sets the localized display name (if different from the browse name).</summary>
    public OpcUaFluentPropertyBuilder DisplayName(string value) { _mapping = _mapping with { DisplayName = value }; return this; }

    /// <summary>Sets a human-readable description.</summary>
    public OpcUaFluentPropertyBuilder Description(string value) { _mapping = _mapping with { Description = value }; return this; }

    /// <summary>Sets the type definition (e.g. "FolderType", "AnalogItemType"), optionally in <paramref name="namespaceUri"/>.</summary>
    public OpcUaFluentPropertyBuilder TypeDefinition(string identifier, string? namespaceUri = null) { _mapping = _mapping with { TypeDefinition = identifier, TypeDefinitionNamespace = namespaceUri }; return this; }

    /// <summary>Overrides the node class (otherwise auto-detected from the C# type).</summary>
    public OpcUaFluentPropertyBuilder NodeClass(OpcUaNodeClass value) { _mapping = _mapping with { NodeClass = value }; return this; }

    /// <summary>Overrides the data type (otherwise inferred from the C# type), e.g. "Double" or "NodeId", optionally in <paramref name="namespaceUri"/>.</summary>
    public OpcUaFluentPropertyBuilder DataType(string identifier, string? namespaceUri = null) { _mapping = _mapping with { DataType = identifier, DataTypeNamespace = namespaceUri }; return this; }

    /// <summary>Marks this property as the main value of a VariableNode.</summary>
    public OpcUaFluentPropertyBuilder IsValue(bool value = true) { _mapping = _mapping with { IsValue = value }; return this; }

    /// <summary>Sets the reference type from the parent (e.g. "HasComponent", "HasProperty"; default "HasProperty"), optionally in <paramref name="namespaceUri"/>.</summary>
    public OpcUaFluentPropertyBuilder ReferenceType(string identifier, string? namespaceUri = null) { _mapping = _mapping with { ReferenceType = identifier, ReferenceTypeNamespace = namespaceUri }; return this; }

    /// <summary>Sets the reference type for collection/dictionary items, optionally in <paramref name="namespaceUri"/>.</summary>
    public OpcUaFluentPropertyBuilder ItemReferenceType(string identifier, string? namespaceUri = null) { _mapping = _mapping with { ItemReferenceType = identifier, ItemReferenceTypeNamespace = namespaceUri }; return this; }

    /// <summary>Client only: sets the sampling interval in milliseconds.</summary>
    public OpcUaFluentPropertyBuilder SamplingInterval(int value) { _mapping = _mapping with { SamplingInterval = value }; return this; }

    /// <summary>Client only: sets the queue size for monitored items.</summary>
    public OpcUaFluentPropertyBuilder QueueSize(uint value) { _mapping = _mapping with { QueueSize = value }; return this; }

    /// <summary>Client only: sets whether to discard the oldest value when the queue is full.</summary>
    public OpcUaFluentPropertyBuilder DiscardOldest(bool value) { _mapping = _mapping with { DiscardOldest = value }; return this; }

    /// <summary>Client only: sets the trigger for data change notifications.</summary>
    public OpcUaFluentPropertyBuilder DataChangeTrigger(DataChangeTrigger value) { _mapping = _mapping with { DataChangeTrigger = value }; return this; }

    /// <summary>Client only: sets the deadband type for filtering value changes.</summary>
    public OpcUaFluentPropertyBuilder DeadbandType(DeadbandType value) { _mapping = _mapping with { DeadbandType = value }; return this; }

    /// <summary>Client only: sets the deadband value for filtering (interpreted per the deadband type).</summary>
    public OpcUaFluentPropertyBuilder DeadbandValue(double value) { _mapping = _mapping with { DeadbandValue = value }; return this; }

    /// <summary>Server only: sets the modelling rule (Mandatory, Optional, etc.).</summary>
    public OpcUaFluentPropertyBuilder ModellingRule(ModellingRule value) { _mapping = _mapping with { ModellingRule = value }; return this; }

    /// <summary>Server only: sets the event notifier flags for objects that emit events.</summary>
    public OpcUaFluentPropertyBuilder EventNotifier(byte value) { _mapping = _mapping with { EventNotifier = value }; return this; }

    /// <summary>Server only: adds a non-hierarchical reference (e.g. HasInterface) from this node to a target node.</summary>
    /// <param name="referenceType">The reference type (e.g. "HasInterface").</param>
    /// <param name="referenceTypeNamespace">The namespace URI for the reference type, or null for a standard type.</param>
    /// <param name="targetNodeId">The identifier of the target node.</param>
    /// <param name="targetNamespaceUri">The namespace URI for the target node, or null.</param>
    /// <param name="isForward">True for a forward reference (default), false for an inverse reference.</param>
    public OpcUaFluentPropertyBuilder AdditionalReference(
        string referenceType,
        string? referenceTypeNamespace,
        string targetNodeId,
        string? targetNamespaceUri = null,
        bool isForward = true)
    {
        var reference = new OpcUaAdditionalReference
        {
            ReferenceType = referenceType,
            ReferenceTypeNamespace = referenceTypeNamespace,
            TargetNodeId = targetNodeId,
            TargetNamespaceUri = targetNamespaceUri,
            IsForward = isForward
        };
        _mapping = _mapping with { AdditionalReferences = [.. _mapping.AdditionalReferences ?? [], reference] };
        return this;
    }

    internal (string? Segment, OpcUaPropertyMapping Metadata) Build() => (_segment, _mapping);
}
