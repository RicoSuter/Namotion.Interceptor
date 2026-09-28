using Namotion.Interceptor.Modbus.Mapping;

namespace Namotion.Interceptor.Modbus.Tests.Mapping;

public class ModbusRegisterCodecTests
{
    [Theory]
    [InlineData(ModbusDataType.Boolean, 0, 1)]
    [InlineData(ModbusDataType.U16, 0, 1)]
    [InlineData(ModbusDataType.S16, 0, 1)]
    [InlineData(ModbusDataType.U32, 0, 2)]
    [InlineData(ModbusDataType.S32, 0, 2)]
    [InlineData(ModbusDataType.F32, 0, 2)]
    [InlineData(ModbusDataType.String, 8, 8)]
    public void WhenGettingRegisterCount_ThenMatchesDataType(ModbusDataType dataType, int length, int expected)
    {
        // Act
        var count = ModbusRegisterCodec.GetRegisterCount(dataType, length);

        // Assert
        Assert.Equal(expected, count);
    }

    [Theory]
    [InlineData(ModbusDataType.U16, new byte[] { 0xFF, 0xFE }, 65534L)]
    [InlineData(ModbusDataType.S16, new byte[] { 0xFF, 0xFE }, -2L)]
    [InlineData(ModbusDataType.U32, new byte[] { 0xFF, 0xFF, 0xFF, 0xFE }, 4294967294L)]
    [InlineData(ModbusDataType.S32, new byte[] { 0xFF, 0xFF, 0xFF, 0xFE }, -2L)]
    [InlineData(ModbusDataType.Boolean, new byte[] { 1 }, 1L)]
    public void WhenReadingInteger_ThenSignIsExtendedPerDataType(ModbusDataType dataType, byte[] raw, long expected)
    {
        // Act
        var value = ModbusRegisterCodec.ReadInteger(raw, dataType, ModbusWordOrder.HighWordFirst);

        // Assert
        Assert.Equal(expected, value);
    }

    [Fact]
    public void WhenReadingU16WithByteSwappedWordOrder_ThenBytesAreNotSwapped()
    {
        // Arrange
        byte[] raw = [0x12, 0x34];

        // Act
        var value = ModbusRegisterCodec.ReadInteger(raw, ModbusDataType.U16, ModbusWordOrder.HighWordFirstByteSwapped);

        // Assert
        Assert.Equal(0x1234L, value);
    }

    [Theory]
    [InlineData(ModbusWordOrder.HighWordFirst, new byte[] { 0x01, 0x02, 0x03, 0x04 })]
    [InlineData(ModbusWordOrder.LowWordFirst, new byte[] { 0x03, 0x04, 0x01, 0x02 })]
    [InlineData(ModbusWordOrder.HighWordFirstByteSwapped, new byte[] { 0x02, 0x01, 0x04, 0x03 })]
    [InlineData(ModbusWordOrder.LowWordFirstByteSwapped, new byte[] { 0x04, 0x03, 0x02, 0x01 })]
    public void WhenReadingU32_ThenWordOrderIsApplied(ModbusWordOrder wordOrder, byte[] raw)
    {
        // Act
        var value = ModbusRegisterCodec.ReadU32(raw, wordOrder);

        // Assert
        Assert.Equal(0x01020304u, value);
    }

    // -2 is 0xFFFFFFFE
    [Theory]
    [InlineData(ModbusWordOrder.HighWordFirst, new byte[] { 0xFF, 0xFF, 0xFF, 0xFE })]
    [InlineData(ModbusWordOrder.LowWordFirst, new byte[] { 0xFF, 0xFE, 0xFF, 0xFF })]
    [InlineData(ModbusWordOrder.HighWordFirstByteSwapped, new byte[] { 0xFF, 0xFF, 0xFE, 0xFF })]
    [InlineData(ModbusWordOrder.LowWordFirstByteSwapped, new byte[] { 0xFE, 0xFF, 0xFF, 0xFF })]
    public void WhenReadingS32_ThenWordOrderIsAppliedBeforeSignExtension(ModbusWordOrder wordOrder, byte[] raw)
    {
        // Act
        var value = ModbusRegisterCodec.ReadInteger(raw, ModbusDataType.S32, wordOrder);

        // Assert
        Assert.Equal(-2L, value);
    }

    // 1.5f is 0x3FC00000
    [Theory]
    [InlineData(ModbusWordOrder.HighWordFirst, new byte[] { 0x3F, 0xC0, 0x00, 0x00 })]
    [InlineData(ModbusWordOrder.LowWordFirst, new byte[] { 0x00, 0x00, 0x3F, 0xC0 })]
    [InlineData(ModbusWordOrder.HighWordFirstByteSwapped, new byte[] { 0xC0, 0x3F, 0x00, 0x00 })]
    [InlineData(ModbusWordOrder.LowWordFirstByteSwapped, new byte[] { 0x00, 0x00, 0xC0, 0x3F })]
    public void WhenReadingSingle_ThenIeeeBitsAreDecodedInWordOrder(ModbusWordOrder wordOrder, byte[] raw)
    {
        // Act
        var value = ModbusRegisterCodec.ReadSingle(raw, wordOrder);

        // Assert
        Assert.Equal(1.5f, value);
    }

    [Fact]
    public void WhenReadingString_ThenTrailingNullsAndSpacesAreTrimmed()
    {
        // Arrange
        byte[] raw = [(byte)'S', (byte)'o', (byte)'l', (byte)'a', (byte)'r', (byte)' ', 0, 0];

        // Act
        var value = ModbusRegisterCodec.ReadString(raw);

        // Assert
        Assert.Equal("Solar", value);
    }

    [Fact]
    public void WhenReadingStringOfOnlyNulls_ThenReturnsEmptyString()
    {
        // Arrange
        byte[] raw = [0, 0, 0, 0];

        // Act
        var value = ModbusRegisterCodec.ReadString(raw);

        // Assert
        Assert.Equal("", value);
    }

    [Theory]
    [InlineData(ModbusDataType.U16, ModbusNotAvailableValue.SignedMaximum, ModbusWordOrder.HighWordFirst, new byte[] { 0x7F, 0xFF }, true)]
    [InlineData(ModbusDataType.S16, ModbusNotAvailableValue.SignedMaximum, ModbusWordOrder.HighWordFirst, new byte[] { 0x7F, 0xFE }, false)]
    [InlineData(ModbusDataType.S16, ModbusNotAvailableValue.SignedMinimum, ModbusWordOrder.HighWordFirst, new byte[] { 0x80, 0x00 }, true)]
    [InlineData(ModbusDataType.U16, ModbusNotAvailableValue.UnsignedMaximum, ModbusWordOrder.HighWordFirst, new byte[] { 0xFF, 0xFF }, true)]
    [InlineData(ModbusDataType.S32, ModbusNotAvailableValue.SignedMaximum, ModbusWordOrder.HighWordFirst, new byte[] { 0x7F, 0xFF, 0xFF, 0xFF }, true)]
    [InlineData(ModbusDataType.S32, ModbusNotAvailableValue.SignedMaximum, ModbusWordOrder.HighWordFirst, new byte[] { 0x00, 0x00, 0x7F, 0xFF }, false)]
    [InlineData(ModbusDataType.S32, ModbusNotAvailableValue.SignedMinimum, ModbusWordOrder.HighWordFirst, new byte[] { 0x80, 0x00, 0x00, 0x00 }, true)]
    [InlineData(ModbusDataType.U32, ModbusNotAvailableValue.UnsignedMaximum, ModbusWordOrder.HighWordFirst, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, true)]
    [InlineData(ModbusDataType.U16, ModbusNotAvailableValue.None, ModbusWordOrder.HighWordFirst, new byte[] { 0x7F, 0xFF }, false)]
    [InlineData(ModbusDataType.S32, ModbusNotAvailableValue.SignedMaximum, ModbusWordOrder.LowWordFirst, new byte[] { 0xFF, 0xFF, 0x7F, 0xFF }, true)]
    [InlineData(ModbusDataType.S32, ModbusNotAvailableValue.SignedMaximum, ModbusWordOrder.LowWordFirst, new byte[] { 0x7F, 0xFF, 0xFF, 0xFF }, false)]
    public void WhenCheckingNotAvailable_ThenPatternIsMatchedForTheWidth(
        ModbusDataType dataType, ModbusNotAvailableValue notAvailableValue, ModbusWordOrder wordOrder, byte[] raw, bool expected)
    {
        // Act
        var isNotAvailable = ModbusRegisterCodec.IsNotAvailable(raw, dataType, wordOrder, notAvailableValue);

        // Assert
        Assert.Equal(expected, isNotAvailable);
    }
}
