namespace Namotion.Interceptor.Modbus.Attributes;

/// <summary>
/// Sets the Modbus unit ID for the registers of this subject class and its children.
/// <see cref="IModbusUnitIdProvider"/> takes precedence when both are present.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class ModbusUnitIdAttribute : Attribute
{
    public ModbusUnitIdAttribute(byte unitId)
    {
        UnitId = unitId;
    }

    public byte UnitId { get; }
}
