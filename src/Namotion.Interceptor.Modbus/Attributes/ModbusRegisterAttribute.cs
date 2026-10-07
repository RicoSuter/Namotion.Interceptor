namespace Namotion.Interceptor.Modbus.Attributes;

/// <summary>
/// Maps a subject property to a Modbus register or bit. <see cref="Address"/> is relative to the declaring
/// subject's <see cref="IModbusBaseAddressProvider.BaseAddress"/>, or absolute when the subject has none.
/// Device libraries can derive from it to preset values.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public class ModbusRegisterAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance mapping the register or bit at <paramref name="address"/> as <paramref name="dataType"/>.
    /// </summary>
    public ModbusRegisterAttribute(int address, ModbusDataType dataType)
    {
        Address = address;
        DataType = dataType;
    }

    /// <summary>
    /// Gets the raw protocol address of the (first) register or the bit. With the base address added it must lie in 0 to 65535.
    /// </summary>
    public int Address { get; }

    /// <summary>
    /// Gets how the raw registers or bit are decoded.
    /// </summary>
    public ModbusDataType DataType { get; }

    /// <summary>
    /// Gets the address space read from. The bit spaces require <see cref="ModbusDataType.Boolean"/>. Default is
    /// <see cref="ModbusAddressSpace.HoldingRegister"/>.
    /// </summary>
    public ModbusAddressSpace AddressSpace { get; init; } = ModbusAddressSpace.HoldingRegister;

    /// <summary>
    /// Gets the register order of 32-bit and 64-bit values. Ignored for other types.
    /// </summary>
    public ModbusWordOrder WordOrder { get; init; } = ModbusWordOrder.HighWordFirst;

    /// <summary>
    /// Gets the static factor the raw value is multiplied with, in addition to the dynamic scale factor when one applies:
    /// value = raw * Scale * 10^exponent. Requires a floating point, decimal or <see cref="TimeSpan"/> property, where a <see cref="TimeSpan"/> takes the value as seconds.
    /// </summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>
    /// Gets the name of an <see cref="ModbusDataType.S16"/> register property on the same subject holding a power-of-ten exponent:
    /// value = raw * <see cref="Scale"/> * 10^exponent. For a scale factor on another subject, implement
    /// <see cref="IModbusScaleFactorProvider"/> instead.
    /// </summary>
    public string? ScaleFactorProperty { get; init; }

    /// <summary>
    /// Gets the register count of <see cref="ModbusDataType.String"/> values, at least 1 and within the address space.
    /// Must be 0 for other types.
    /// </summary>
    /// <remarks>
    /// A string longer than one request (125 registers) is read in consecutive requests, which Modbus cannot read
    /// atomically: a changed value is only applied once two complete reads agree, possibly in the next cycle, so a
    /// string that changes faster than one read pair is never applied.
    /// </remarks>
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
