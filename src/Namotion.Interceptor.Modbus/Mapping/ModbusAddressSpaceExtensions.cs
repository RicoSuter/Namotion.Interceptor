namespace Namotion.Interceptor.Modbus.Mapping;

internal static class ModbusAddressSpaceExtensions
{
    /// <summary>
    /// The number of addresses in every address space: 0 to 65535.
    /// </summary>
    public const int AddressCount = 65536;

    /// <summary>
    /// Gets whether the space holds single bits (coils and discrete inputs) rather than 16-bit registers.
    /// </summary>
    public static bool IsBitSpace(this ModbusAddressSpace space)
        => space is ModbusAddressSpace.Coil or ModbusAddressSpace.DiscreteInput;
}
