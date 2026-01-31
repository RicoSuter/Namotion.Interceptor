namespace Namotion.Interceptor.OpcUa.Attributes;

/// <summary>
/// Specifies the OPC UA reference type from parent to this node.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class OpcUaReferenceAttribute : Attribute
{
    /// <summary>
    /// Creates a new OPC UA reference attribute.
    /// </summary>
    /// <param name="referenceType">The reference type (e.g., "HasComponent", "HasProperty", "HasAddIn").</param>
    public OpcUaReferenceAttribute(string referenceType = "HasProperty")
    {
        ReferenceType = referenceType;
    }

    /// <summary>
    /// Reference type for this property (e.g., "HasComponent", "HasProperty", "HasAddIn", "Organizes").
    /// Default is "HasProperty".
    /// </summary>
    public string ReferenceType { get; }

    /// <summary>
    /// Namespace URI for the ReferenceType.
    /// Used for custom reference types from imported nodesets.
    /// </summary>
    public string? ReferenceTypeNamespace { get; init; }

    /// <summary>
    /// Reference type for collection/dictionary items.
    /// If not specified, uses the same as ReferenceType.
    /// </summary>
    public string? ItemReferenceType { get; init; }

    /// <summary>
    /// Namespace URI for the ItemReferenceType.
    /// Used for custom reference types for collection items from imported nodesets.
    /// </summary>
    public string? ItemReferenceTypeNamespace { get; init; }

    /// <summary>
    /// Gets or sets the node structure for collections. Default is Container (backward compatible).
    /// Dictionaries always use Container structure (this property is ignored for dictionaries).
    /// </summary>
    public CollectionNodeStructure CollectionStructure { get; init; } = CollectionNodeStructure.Container;
}
