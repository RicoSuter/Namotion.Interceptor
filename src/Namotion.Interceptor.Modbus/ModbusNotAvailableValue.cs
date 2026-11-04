namespace Namotion.Interceptor.Modbus;

/// <summary>
/// A raw bit pattern a device uses for "not available", mapped to <c>null</c>.
/// </summary>
public enum ModbusNotAvailableValue
{
    /// <summary>No pattern is treated as not available.</summary>
    None,

    /// <summary>0x7FFF, 0x7FFFFFFF or 0x7FFFFFFFFFFFFFFF for 16-bit, 32-bit or 64-bit values.</summary>
    SignedMaximum,

    /// <summary>0x8000, 0x80000000 or 0x8000000000000000 for 16-bit, 32-bit or 64-bit values.</summary>
    SignedMinimum,

    /// <summary>0xFFFF, 0xFFFFFFFF or 0xFFFFFFFFFFFFFFFF for 16-bit, 32-bit or 64-bit values.</summary>
    UnsignedMaximum
}
