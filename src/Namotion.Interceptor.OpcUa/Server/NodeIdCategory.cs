namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// Categorizes the type of OPC UA node being resolved.
/// Used by <see cref="OpcUaNodeIdResolver"/> to determine lookup strategy.
/// </summary>
internal enum NodeIdCategory
{
    /// <summary>
    /// Object types (e.g., FolderType, BaseObjectType, custom object types).
    /// Lookup searches <see cref="Opc.Ua.ObjectTypeIds"/>.
    /// </summary>
    ObjectType,

    /// <summary>
    /// Variable types (e.g., BaseDataVariableType, AnalogItemType).
    /// Lookup searches <see cref="Opc.Ua.VariableTypeIds"/>.
    /// </summary>
    VariableType,

    /// <summary>
    /// Reference types (e.g., HasComponent, HasProperty, Organizes).
    /// Lookup searches <see cref="Opc.Ua.ReferenceTypeIds"/>.
    /// </summary>
    ReferenceType,

    /// <summary>
    /// Data types (e.g., Double, String, Int32, custom data types).
    /// Lookup searches <see cref="Opc.Ua.DataTypeIds"/>.
    /// </summary>
    DataType,

    /// <summary>
    /// Direct node reference (no type lookup, only cache/parse/BrowseName).
    /// </summary>
    Node
}
