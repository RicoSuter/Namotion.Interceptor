namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Represents an additional OPC UA reference for non-hierarchical relationships (e.g., HasInterface).
/// </summary>
public record OpcUaAdditionalReference
{
    /// <summary>
    /// The reference type (e.g., "HasInterface", "GeneratesEvent").
    /// </summary>
    public required string ReferenceType { get; init; }

    /// <summary>
    /// The target node identifier.
    /// </summary>
    public required string TargetNodeId { get; init; }

    /// <summary>
    /// The namespace URI for the target node. If null, uses the default namespace.
    /// </summary>
    public string? TargetNamespaceUri { get; init; }

    /// <summary>
    /// Whether this is a forward reference. Default is true.
    /// </summary>
    public bool IsForward { get; init; } = true;
}
