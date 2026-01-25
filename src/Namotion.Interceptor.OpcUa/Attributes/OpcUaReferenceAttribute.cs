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
    /// Reference type for collection/dictionary items.
    /// If not specified, uses the same as ReferenceType.
    /// </summary>
    public string? ItemReferenceType { get; init; }
}
