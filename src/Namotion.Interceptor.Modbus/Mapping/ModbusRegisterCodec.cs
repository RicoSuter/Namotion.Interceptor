using System.Buffers.Binary;
using System.Text;

namespace Namotion.Interceptor.Modbus.Mapping;

internal static class ModbusRegisterCodec
{
    public static int GetRegisterCount(ModbusDataType dataType, int length) => dataType switch
    {
        ModbusDataType.Boolean or ModbusDataType.U16 or ModbusDataType.S16 => 1,
        ModbusDataType.U32 or ModbusDataType.S32 or ModbusDataType.F32 => 2,
        ModbusDataType.String => length,
        _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, null)
    };

    public static ushort ReadU16(ReadOnlySpan<byte> raw) => BinaryPrimitives.ReadUInt16BigEndian(raw);

    public static uint ReadU32(ReadOnlySpan<byte> raw, ModbusWordOrder wordOrder)
    {
        var first = BinaryPrimitives.ReadUInt16BigEndian(raw);
        var second = BinaryPrimitives.ReadUInt16BigEndian(raw[2..]);
        return wordOrder switch
        {
            ModbusWordOrder.HighWordFirst => ((uint)first << 16) | second,
            ModbusWordOrder.LowWordFirst => ((uint)second << 16) | first,
            ModbusWordOrder.HighWordFirstByteSwapped =>
                ((uint)BinaryPrimitives.ReverseEndianness(first) << 16) | BinaryPrimitives.ReverseEndianness(second),
            ModbusWordOrder.LowWordFirstByteSwapped =>
                ((uint)BinaryPrimitives.ReverseEndianness(second) << 16) | BinaryPrimitives.ReverseEndianness(first),
            _ => throw new ArgumentOutOfRangeException(nameof(wordOrder), wordOrder, null)
        };
    }

    public static long ReadInteger(ReadOnlySpan<byte> raw, ModbusDataType dataType, ModbusWordOrder wordOrder) => dataType switch
    {
        ModbusDataType.Boolean => raw[0] != 0 ? 1 : 0,
        ModbusDataType.U16 => ReadU16(raw),
        ModbusDataType.S16 => (short)ReadU16(raw),
        ModbusDataType.U32 => ReadU32(raw, wordOrder),
        ModbusDataType.S32 => (int)ReadU32(raw, wordOrder),
        _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, null)
    };

    public static float ReadSingle(ReadOnlySpan<byte> raw, ModbusWordOrder wordOrder)
        => BitConverter.UInt32BitsToSingle(ReadU32(raw, wordOrder));

    public static string ReadString(ReadOnlySpan<byte> raw)
    {
        var length = raw.Length;
        while (length > 0 && raw[length - 1] is 0x00 or 0x20)
        {
            length--;
        }

        return Encoding.ASCII.GetString(raw[..length]);
    }

    public static bool IsNotAvailable(
        ReadOnlySpan<byte> raw, ModbusDataType dataType, ModbusWordOrder wordOrder, ModbusNotAvailableValue notAvailableValue)
    {
        if (notAvailableValue == ModbusNotAvailableValue.None)
        {
            return false;
        }

        return dataType switch
        {
            ModbusDataType.U16 or ModbusDataType.S16 => IsNotAvailable16(ReadU16(raw), notAvailableValue),
            ModbusDataType.U32 or ModbusDataType.S32 => IsNotAvailable32(ReadU32(raw, wordOrder), notAvailableValue),
            _ => false
        };
    }

    private static bool IsNotAvailable16(ushort value, ModbusNotAvailableValue notAvailableValue) => notAvailableValue switch
    {
        ModbusNotAvailableValue.SignedMaximum => value == 0x7FFF,
        ModbusNotAvailableValue.SignedMinimum => value == 0x8000,
        ModbusNotAvailableValue.UnsignedMaximum => value == 0xFFFF,
        _ => false
    };

    private static bool IsNotAvailable32(uint value, ModbusNotAvailableValue notAvailableValue) => notAvailableValue switch
    {
        ModbusNotAvailableValue.SignedMaximum => value == 0x7FFFFFFFu,
        ModbusNotAvailableValue.SignedMinimum => value == 0x80000000u,
        ModbusNotAvailableValue.UnsignedMaximum => value == 0xFFFFFFFFu,
        _ => false
    };
}
