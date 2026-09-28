using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Modbus.Mapping;

namespace Namotion.Interceptor.Modbus.Tests.Mapping;

public class ModbusValueConvertersTests
{
    private enum Mode : ushort
    {
        Off = 0,
        On = 1
    }

    [Flags]
    private enum Features : ushort
    {
        None = 0,
        First = 1,
        Second = 2
    }

    private static object? Convert(ModbusRegisterAttribute attribute, Type propertyType, byte[] raw, int exponent = 0)
        => ModbusValueConverters.Create(attribute, propertyType, "Test.Property")(raw, exponent);

    [Fact]
    public void WhenScalingIntoDecimal_ThenResultIsExact()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.S16) { Scale = 0.1 }, typeof(decimal?), [0x00, 0xEA]);

        // Assert
        Assert.Equal(23.4m, value);
    }

    [Fact]
    public void WhenScalingNegativeIntoDouble_ThenSignIsKept()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.S16) { Scale = 0.5 }, typeof(double), [0xFF, 0xFE]);

        // Assert
        Assert.Equal(-1.0, value);
    }

    [Theory]
    [InlineData(-2, 1.23)]
    [InlineData(0, 123)]
    [InlineData(1, 1230)]
    public void WhenUsingDynamicScaleFactor_ThenPowerOfTenIsApplied(int exponent, double expected)
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U16) { ScaleFactorProperty = "Factor" },
            typeof(decimal?), [0x00, 0x7B], exponent);

        // Assert
        Assert.Equal((decimal)expected, value);
    }

    [Fact]
    public void WhenTargetIsEnum_ThenRawValueMapsToMember()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U16), typeof(Mode?), [0x00, 0x01]);

        // Assert
        Assert.Equal(Mode.On, value);
    }

    [Fact]
    public void WhenTargetIsFlagsEnum_ThenBitsArePassedThrough()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U16), typeof(Features), [0x00, 0x03]);

        // Assert
        Assert.Equal(Features.First | Features.Second, value);
    }

    [Fact]
    public void WhenTargetIsBoolFromRegister_ThenNonZeroIsTrue()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U16), typeof(bool?), [0x00, 0x02]);

        // Assert
        Assert.True(Assert.IsType<bool>(value));
    }

    [Fact]
    public void WhenDataTypeIsBoolean_ThenBitIsReturned()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.Boolean) { Space = ModbusAddressSpace.Coil }, typeof(bool), [1]);

        // Assert
        Assert.True(Assert.IsType<bool>(value));
    }

    [Fact]
    public void WhenTargetIsIntegral_ThenValueIsConverted()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.S32), typeof(long?), [0xFF, 0xFF, 0xFF, 0xFE]);

        // Assert
        Assert.Equal(-2L, value);
    }

    [Fact]
    public void WhenDataTypeIsF32_ThenFloatIsReturned()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.F32), typeof(float), [0x3F, 0xC0, 0x00, 0x00]);

        // Assert
        Assert.Equal(1.5f, value);
    }

    [Theory]
    [InlineData(new byte[] { 0x7F, 0xC0, 0x00, 0x00 })] // NaN
    [InlineData(new byte[] { 0x7F, 0x80, 0x00, 0x00 })] // +Infinity
    [InlineData(new byte[] { 0xFF, 0x80, 0x00, 0x00 })] // -Infinity
    [InlineData(new byte[] { 0x6F, 0x80, 0x00, 0x00 })] // 2^96
    [InlineData(new byte[] { 0xEF, 0x80, 0x00, 0x00 })] // -2^96
    public void WhenFloatIsOutsideDecimalRangeForNullableDecimal_ThenNullIsReturned(byte[] raw)
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.F32), typeof(decimal?), raw);

        // Assert
        Assert.Null(value);
    }

    [Fact]
    public void WhenNaNFloatTargetsNonNullableDecimal_ThenOverflowExceptionIsThrown()
    {
        // Act & Assert
        Assert.Throws<OverflowException>(() =>
            Convert(new ModbusRegisterAttribute(0, ModbusDataType.F32), typeof(decimal), [0x7F, 0xC0, 0x00, 0x00]));
    }

    [Fact]
    public void WhenFloatTargetsDecimalWithStaticScale_ThenScaleIsApplied()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.F32) { Scale = 0.1 }, typeof(decimal), [0x3F, 0xC0, 0x00, 0x00]);

        // Assert
        Assert.Equal(0.15m, value);
    }

    [Theory]
    [InlineData(ModbusDataType.U16)]
    [InlineData(ModbusDataType.F32)]
    public void WhenStaticScaleIsOutsideDecimalRange_ThenConfigurationExceptionIsThrown(ModbusDataType dataType)
    {
        // Act & Assert
        Assert.Throws<ModbusConfigurationException>(() =>
            ModbusValueConverters.Create(new ModbusRegisterAttribute(0, dataType) { Scale = 1e30 }, typeof(decimal?), "Test.Property"));
    }

    [Theory]
    [InlineData(29)]
    [InlineData(-29)]
    [InlineData(int.MinValue)]
    public void WhenDynamicScaleExponentIsOutsideDecimalRange_ThenOverflowExceptionIsThrown(int exponent)
    {
        // Act & Assert
        Assert.Throws<OverflowException>(() =>
            Convert(new ModbusRegisterAttribute(0, ModbusDataType.U16) { ScaleFactorProperty = "Factor" }, typeof(decimal?), [0x00, 0x7B], exponent));
    }

    [Fact]
    public void WhenEnumValueIsUndefined_ThenRawValueIsPassedThrough()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U16), typeof(Mode), [0x00, 0x05]);

        // Assert
        Assert.Equal((Mode)5, value);
    }

    [Theory]
    [InlineData(ModbusDataType.F32, typeof(float?))]
    [InlineData(ModbusDataType.Boolean, typeof(bool?))]
    [InlineData(ModbusDataType.String, typeof(string))]
    public void WhenNotAvailableValueIsUsedWithUnsupportedDataType_ThenConfigurationExceptionIsThrown(ModbusDataType dataType, Type propertyType)
    {
        // Act & Assert
        Assert.Throws<ModbusConfigurationException>(() =>
            ModbusValueConverters.Create(
                new ModbusRegisterAttribute(0, dataType)
                {
                    Length = dataType == ModbusDataType.String ? 1 : 0,
                    NotAvailableValue = ModbusNotAvailableValue.SignedMaximum
                },
                propertyType, "Test.Property"));
    }

    [Fact]
    public void WhenNaNFloatTargetsDouble_ThenNaNIsReturned()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.F32), typeof(double), [0x7F, 0xC0, 0x00, 0x00]);

        // Assert
        Assert.Equal(double.NaN, value);
    }

    [Fact]
    public void WhenDataTypeIsString_ThenTextIsReturned()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.String) { Length = 2 }, typeof(string), [(byte)'A', (byte)'B', 0, 0]);

        // Assert
        Assert.Equal("AB", value);
    }

    [Fact]
    public void WhenRawMatchesNotAvailableValue_ThenNullIsReturned()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.S16) { Scale = 0.1, NotAvailableValue = ModbusNotAvailableValue.SignedMaximum },
            typeof(decimal?), [0x7F, 0xFF]);

        // Assert
        Assert.Null(value);
    }

    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(Mode))]
    public void WhenScaledTargetIsIntegralOrEnum_ThenConfigurationExceptionIsThrown(Type propertyType)
    {
        // Act & Assert
        Assert.Throws<ModbusConfigurationException>(() =>
            ModbusValueConverters.Create(new ModbusRegisterAttribute(0, ModbusDataType.U16) { Scale = 0.1 }, propertyType, "Test.Property"));
    }

    [Theory]
    [InlineData(ModbusDataType.U16, typeof(short))]
    [InlineData(ModbusDataType.S16, typeof(ushort))]
    [InlineData(ModbusDataType.U32, typeof(int))]
    [InlineData(ModbusDataType.S32, typeof(uint))]
    [InlineData(ModbusDataType.S16, typeof(uint))]
    [InlineData(ModbusDataType.U16, typeof(byte))]
    public void WhenIntegralTargetCannotHoldTheRange_ThenConfigurationExceptionIsThrown(ModbusDataType dataType, Type propertyType)
    {
        // Act & Assert
        Assert.Throws<ModbusConfigurationException>(() =>
            ModbusValueConverters.Create(new ModbusRegisterAttribute(0, dataType), propertyType, "Test.Property"));
    }

    [Fact]
    public void WhenNotAvailableValueTargetsNonNullable_ThenConfigurationExceptionIsThrown()
    {
        // Act & Assert
        Assert.Throws<ModbusConfigurationException>(() =>
            ModbusValueConverters.Create(
                new ModbusRegisterAttribute(0, ModbusDataType.U16) { NotAvailableValue = ModbusNotAvailableValue.SignedMaximum },
                typeof(int), "Test.Property"));
    }

    [Theory]
    [InlineData(ModbusDataType.String, typeof(int))]
    [InlineData(ModbusDataType.Boolean, typeof(int))]
    [InlineData(ModbusDataType.U16, typeof(string))]
    [InlineData(ModbusDataType.F32, typeof(int))]
    public void WhenClrTypeDoesNotFitDataType_ThenConfigurationExceptionIsThrown(ModbusDataType dataType, Type propertyType)
    {
        // Act & Assert
        Assert.Throws<ModbusConfigurationException>(() =>
            ModbusValueConverters.Create(new ModbusRegisterAttribute(0, dataType) { Length = dataType == ModbusDataType.String ? 1 : 0 },
                propertyType, "Test.Property"));
    }
}
