namespace Namotion.Interceptor.OpcUa.Attributes;

/// <summary>
/// Defines how collections map to OPC UA node structures.
/// </summary>
public enum CollectionNodeStructure
{
    /// <summary>
    /// Children are direct children of parent with BrowseName "PropertyName[index]".
    /// Results in: Parent/Machines[0], Parent/Machines[1]
    /// </summary>
    Flat,

    /// <summary>
    /// Children are placed under an intermediate container node.
    /// Results in: Parent/Machines/Machines[0], Parent/Machines/Machines[1]
    /// </summary>
    Container
}
