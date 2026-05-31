using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Per-member fluent configuration for OPC UA. <see cref="BrowseName"/> sets both the path segment (for
/// composition and reverse navigation) and the node BrowseName. Other setters configure node and
/// monitoring metadata, mirroring the attribute fields.
/// </summary>
public sealed class OpcUaFluentPropertyBuilder
{
    private string? _segment;
    private OpcUaPropertyMapping _mapping = new();

    public OpcUaFluentPropertyBuilder BrowseName(string value)
    {
        _segment = value;
        _mapping = _mapping with { BrowseName = value };
        return this;
    }

    public OpcUaFluentPropertyBuilder BrowseNamespaceUri(string value) { _mapping = _mapping with { BrowseNamespaceUri = value }; return this; }
    public OpcUaFluentPropertyBuilder NodeIdentifier(string value) { _mapping = _mapping with { NodeIdentifier = value }; return this; }
    public OpcUaFluentPropertyBuilder NodeNamespaceUri(string value) { _mapping = _mapping with { NodeNamespaceUri = value }; return this; }
    public OpcUaFluentPropertyBuilder DisplayName(string value) { _mapping = _mapping with { DisplayName = value }; return this; }
    public OpcUaFluentPropertyBuilder Description(string value) { _mapping = _mapping with { Description = value }; return this; }
    public OpcUaFluentPropertyBuilder TypeDefinition(string identifier, string? namespaceUri = null) { _mapping = _mapping with { TypeDefinition = identifier, TypeDefinitionNamespace = namespaceUri }; return this; }
    public OpcUaFluentPropertyBuilder NodeClass(OpcUaNodeClass value) { _mapping = _mapping with { NodeClass = value }; return this; }
    public OpcUaFluentPropertyBuilder DataType(string identifier, string? namespaceUri = null) { _mapping = _mapping with { DataType = identifier, DataTypeNamespace = namespaceUri }; return this; }
    public OpcUaFluentPropertyBuilder IsValue(bool value = true) { _mapping = _mapping with { IsValue = value }; return this; }
    public OpcUaFluentPropertyBuilder ReferenceType(string identifier, string? namespaceUri = null) { _mapping = _mapping with { ReferenceType = identifier, ReferenceTypeNamespace = namespaceUri }; return this; }
    public OpcUaFluentPropertyBuilder ItemReferenceType(string identifier, string? namespaceUri = null) { _mapping = _mapping with { ItemReferenceType = identifier, ItemReferenceTypeNamespace = namespaceUri }; return this; }
    public OpcUaFluentPropertyBuilder SamplingInterval(int value) { _mapping = _mapping with { SamplingInterval = value }; return this; }
    public OpcUaFluentPropertyBuilder QueueSize(uint value) { _mapping = _mapping with { QueueSize = value }; return this; }
    public OpcUaFluentPropertyBuilder DiscardOldest(bool value) { _mapping = _mapping with { DiscardOldest = value }; return this; }
    public OpcUaFluentPropertyBuilder DataChangeTrigger(DataChangeTrigger value) { _mapping = _mapping with { DataChangeTrigger = value }; return this; }
    public OpcUaFluentPropertyBuilder DeadbandType(DeadbandType value) { _mapping = _mapping with { DeadbandType = value }; return this; }
    public OpcUaFluentPropertyBuilder DeadbandValue(double value) { _mapping = _mapping with { DeadbandValue = value }; return this; }
    public OpcUaFluentPropertyBuilder ModellingRule(ModellingRule value) { _mapping = _mapping with { ModellingRule = value }; return this; }
    public OpcUaFluentPropertyBuilder EventNotifier(byte value) { _mapping = _mapping with { EventNotifier = value }; return this; }

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
