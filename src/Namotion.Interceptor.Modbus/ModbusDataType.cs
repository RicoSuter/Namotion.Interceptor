namespace Namotion.Interceptor.Modbus;

/// <summary>
/// The wire type of a mapped Modbus value.
/// </summary>
public enum ModbusDataType
{
    /// <summary>A single bit of a coil or discrete input.</summary>
    Boolean,

    /// <summary>Unsigned 16-bit integer, one register.</summary>
    U16,

    /// <summary>Signed 16-bit integer, one register.</summary>
    S16,

    /// <summary>Unsigned 32-bit integer, two registers.</summary>
    U32,

    /// <summary>Signed 32-bit integer, two registers.</summary>
    S32,

    /// <summary>IEEE 754 single precision float, two registers.</summary>
    F32,

    /// <summary>ASCII string, two characters per register, <c>Length</c> registers.</summary>
    String
}
