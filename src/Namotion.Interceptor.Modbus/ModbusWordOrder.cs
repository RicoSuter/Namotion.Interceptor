namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Register and byte order of 32-bit values. Registers are A B (first) and C D (second) on the wire.
/// </summary>
public enum ModbusWordOrder
{
    /// <summary>A B C D: the first register holds the high word.</summary>
    HighWordFirst,

    /// <summary>C D A B: the first register holds the low word.</summary>
    LowWordFirst,

    /// <summary>B A D C: high word first with the bytes of each register swapped.</summary>
    HighWordFirstByteSwapped,

    /// <summary>D C B A: low word first with the bytes of each register swapped.</summary>
    LowWordFirstByteSwapped
}
