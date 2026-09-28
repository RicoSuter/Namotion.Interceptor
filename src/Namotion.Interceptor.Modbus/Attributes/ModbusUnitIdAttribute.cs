namespace Namotion.Interceptor.Modbus.Attributes;

/// <summary>
/// Sets the Modbus unit ID for the registers of this subject class and its children.
/// <see cref="IModbusUnitIdProvider"/> takes precedence when both are present.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class ModbusUnitIdAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance with the unit ID to read from.
    /// </summary>
    public ModbusUnitIdAttribute(byte unitId)
    {
        UnitId = unitId;
    }

    /// <summary>
    /// Gets the unit ID the registers of this subject and its children are read from.
    /// </summary>
    public byte UnitId { get; }
}
