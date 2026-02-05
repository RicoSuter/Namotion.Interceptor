namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Specifies the OPC UA NodeClass for a property or class.
/// </summary>
public enum OpcUaNodeClass
{
    /// <summary>
    /// Auto-detect: classes become ObjectNodes, primitive properties become VariableNodes.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Force ObjectNode regardless of C# type.
    /// </summary>
    Object = 1,

    /// <summary>
    /// Force VariableNode. Use for classes representing VariableTypes (e.g., AnalogSignalVariableType).
    /// </summary>
    Variable = 2
}
