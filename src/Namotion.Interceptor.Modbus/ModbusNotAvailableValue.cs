namespace Namotion.Interceptor.Modbus;

/// <summary>
/// A raw bit pattern a device uses for "not available", mapped to <c>null</c>.
/// </summary>
public enum ModbusNotAvailableValue
{
    /// <summary>No pattern is treated as not available.</summary>
    None,

    /// <summary>0x7FFF for 16-bit values, 0x7FFFFFFF for 32-bit values.</summary>
    SignedMaximum,

    /// <summary>0x8000 for 16-bit values, 0x80000000 for 32-bit values.</summary>
    SignedMinimum,

    /// <summary>0xFFFF for 16-bit values, 0xFFFFFFFF for 32-bit values.</summary>
    UnsignedMaximum
}
