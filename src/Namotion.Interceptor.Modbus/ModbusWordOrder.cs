namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Register and byte order of 32-bit and 64-bit values. Registers are A B (first) and C D (second) on the wire for
/// 32-bit values; 64-bit values apply the same order to four registers.
/// </summary>
public enum ModbusWordOrder
{
    /// <summary>A B C D: the first register holds the high word, so 64-bit values are read most significant word first.</summary>
    HighWordFirst,

    /// <summary>C D A B: the first register holds the low word, so 64-bit values are read least significant word first.</summary>
    LowWordFirst,

    /// <summary>B A D C: high word first with the bytes of each register swapped.</summary>
    HighWordFirstByteSwapped,

    /// <summary>D C B A: low word first with the bytes of each register swapped.</summary>
    LowWordFirstByteSwapped
}
