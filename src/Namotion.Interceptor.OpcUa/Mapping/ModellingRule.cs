namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// OPC UA modelling rules for type definitions.
/// </summary>
public enum ModellingRule
{
    /// <summary>
    /// No modelling rule specified.
    /// </summary>
    Unset = -1,

    /// <summary>
    /// Instance must exist in all instances of the containing type.
    /// </summary>
    Mandatory = 0,

    /// <summary>
    /// Instance may or may not exist.
    /// </summary>
    Optional = 1,

    /// <summary>
    /// Placeholder for mandatory instances in a collection.
    /// </summary>
    MandatoryPlaceholder = 2,

    /// <summary>
    /// Placeholder for optional instances in a collection.
    /// </summary>
    OptionalPlaceholder = 3
}
