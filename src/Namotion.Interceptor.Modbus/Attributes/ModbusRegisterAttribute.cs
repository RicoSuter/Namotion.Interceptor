namespace Namotion.Interceptor.Modbus.Attributes;

/// <summary>
/// Maps a subject property to a Modbus register or bit. <see cref="Address"/> is relative to the declaring
/// subject's <see cref="IModbusBaseAddressProvider.BaseAddress"/>, or absolute when the subject has none.
/// </summary>
/// <remarks>Not sealed, so device libraries can derive attributes with preset values.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public class ModbusRegisterAttribute : Attribute
{
    public ModbusRegisterAttribute(int address, ModbusDataType dataType)
    {
        Address = address;
        DataType = dataType;
    }

    public int Address { get; }

    public ModbusDataType DataType { get; }

    public ModbusAddressSpace Space { get; init; } = ModbusAddressSpace.HoldingRegister;

    /// <summary>
    /// Gets the register order of 32-bit values. Ignored for other types.
    /// </summary>
    public ModbusWordOrder WordOrder { get; init; } = ModbusWordOrder.HighWordFirst;

    /// <summary>
    /// Gets the static factor the raw value is multiplied with. Requires a floating point or decimal property.
    /// </summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>
    /// Gets the name of an integer register property on the same subject holding a power-of-ten exponent:
    /// value = raw * 10^exponent. Mutually exclusive with <see cref="Scale"/>.
    /// </summary>
    public string? ScaleFactorProperty { get; init; }

    /// <summary>
    /// Gets the register count of <see cref="ModbusDataType.String"/> values. Must be 0 for other types.
    /// </summary>
    public int Length { get; init; }

    /// <summary>
    /// Gets whether the mapping may be written. Reserved for writing and not enforced by this read-only connector.
    /// Ignored for <see cref="ModbusAddressSpace.InputRegister"/> and <see cref="ModbusAddressSpace.DiscreteInput"/>,
    /// which are read only by definition.
    /// </summary>
    public ModbusAccess Access { get; init; } = ModbusAccess.ReadWrite;

    /// <summary>
    /// Gets the raw pattern that maps to <c>null</c>. Requires a nullable property and an integer data type.
    /// </summary>
    public ModbusNotAvailableValue NotAvailableValue { get; init; } = ModbusNotAvailableValue.None;
}
