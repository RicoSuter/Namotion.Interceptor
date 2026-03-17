namespace Namotion.Interceptor.OpcUa.Attributes;

/// <summary>
/// Marks the property that holds the main value for a VariableNode class.
/// Use this when a class represents a complex VariableType (e.g., AnalogSignalVariableType)
/// where one property is the OPC UA Value attribute and others are child Properties.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class OpcUaValueAttribute : Attribute
{
}
