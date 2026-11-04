namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Whether a mapping may be written. Only reads are supported in this version.
/// </summary>
public enum ModbusAccess
{
    /// <summary>The device accepts writes to this mapping.</summary>
    ReadWrite,

    /// <summary>The mapping must never be written.</summary>
    ReadOnly
}
