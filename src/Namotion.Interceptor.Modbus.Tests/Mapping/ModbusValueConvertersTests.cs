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

    private enum Direction : short
    {
        Reverse = -2,
        Forward = 1
    }

    private enum Offset : long
    {
        Negative = -2,
        Positive = 2
    }

    private enum Marker : ulong
    {
        Low = 1,
        High = 0x8000000000000001
    }

    private static object? Convert(ModbusRegisterAttribute attribute, Type propertyType, byte[] raw, int exponent = 0)
        => ModbusValueConverters.Create(attribute, propertyType, "Test.Property", hasDynamicScale: attribute.ScaleFactorProperty is not null)(raw, exponent);

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
    public void WhenUsingNegativeDynamicScaleFactorIntoDouble_ThenResultHasNoBinaryRoundingError()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U16) { ScaleFactorProperty = "Factor" },
            typeof(double?), [0x00, 0x03], exponent: -1);

        // Assert
        Assert.Equal(0.3, value);
    }

    [Theory]
    [InlineData(-2, 1.23f)]
    [InlineData(1, 1230f)]
    public void WhenUsingDynamicScaleFactorIntoFloat_ThenPowerOfTenIsApplied(int exponent, float expected)
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U16) { ScaleFactorProperty = "Factor" },
            typeof(float?), [0x00, 0x7B], exponent);

        // Assert
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(ModbusDataType.S16, new byte[] { 0x00, 0xEA }, 23.4f)]
    [InlineData(ModbusDataType.F32, new byte[] { 0x40, 0x20, 0x00, 0x00 }, 0.25f)]
    public void WhenScalingIntoFloat_ThenStaticScaleIsApplied(ModbusDataType dataType, byte[] raw, float expected)
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, dataType) { Scale = 0.1 }, typeof(float), raw);

        // Assert
        Assert.Equal(expected, value);
    }

    [Fact]
    public void WhenIntegerTargetsFloat_ThenValueIsConverted()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U16), typeof(float), [0x01, 0x02]);

        // Assert
        Assert.Equal(258f, value);
    }

    [Theory]
    [InlineData(typeof(Mode?))]
    [InlineData(typeof(bool?))]
    public void WhenRawMatchesNotAvailableValueForEnumOrBool_ThenNullIsReturned(Type propertyType)
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U16) { NotAvailableValue = ModbusNotAvailableValue.UnsignedMaximum },
            propertyType, [0xFF, 0xFF]);

        // Assert
        Assert.Null(value);
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
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.Boolean) { AddressSpace = ModbusAddressSpace.Coil }, typeof(bool), [1]);

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
            ModbusValueConverters.Create(new ModbusRegisterAttribute(0, dataType) { Scale = 1e30 }, typeof(decimal?), "Test.Property", hasDynamicScale: false));
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
                propertyType, "Test.Property", hasDynamicScale: false));
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
            ModbusValueConverters.Create(new ModbusRegisterAttribute(0, ModbusDataType.U16) { Scale = 0.1 }, propertyType, "Test.Property", hasDynamicScale: false));
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
            ModbusValueConverters.Create(new ModbusRegisterAttribute(0, dataType), propertyType, "Test.Property", hasDynamicScale: false));
    }

    [Fact]
    public void WhenNotAvailableValueTargetsNonNullable_ThenConfigurationExceptionIsThrown()
    {
        // Act & Assert
        Assert.Throws<ModbusConfigurationException>(() =>
            ModbusValueConverters.Create(
                new ModbusRegisterAttribute(0, ModbusDataType.U16) { NotAvailableValue = ModbusNotAvailableValue.SignedMaximum },
                typeof(int), "Test.Property", hasDynamicScale: false));
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
                propertyType, "Test.Property", hasDynamicScale: false));
    }

    [Fact]
    public void WhenConvertingMaximumU64IntoDecimal_ThenValueIsExact()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U64), typeof(decimal?), [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

        // Assert
        Assert.Equal(18446744073709551615m, value);
    }

    [Fact]
    public void WhenConvertingU64IntoUnsignedLong_ThenValueIsKept()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U64), typeof(ulong?), [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

        // Assert
        Assert.Equal(ulong.MaxValue, value);
    }

    [Fact]
    public void WhenConvertingS64IntoLong_ThenSignIsKept()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.S64), typeof(long?), [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE]);

        // Assert
        Assert.Equal(-2L, value);
    }

    [Fact]
    public void WhenScalingU64WithDynamicScaleFactor_ThenPowerOfTenIsApplied()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U64) { ScaleFactorProperty = "Factor" },
            typeof(decimal?), [0, 0, 0, 0, 0, 0, 0, 150], exponent: 3);

        // Assert
        Assert.Equal(150000m, value);
    }

    [Fact]
    public void WhenU64IsUnsignedMaximumPattern_ThenValueIsNull()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U64) { NotAvailableValue = ModbusNotAvailableValue.UnsignedMaximum },
            typeof(decimal?), [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

        // Assert
        Assert.Null(value);
    }

    [Fact]
    public void WhenConvertingU64IntoLong_ThenConfigurationExceptionIsThrown()
    {
        // Act & Assert
        Assert.Throws<ModbusConfigurationException>(() => Convert(new ModbusRegisterAttribute(0, ModbusDataType.U64), typeof(long?), new byte[8]));
    }

    [Fact]
    public void WhenConvertingS64IntoUnsignedLong_ThenConfigurationExceptionIsThrown()
    {
        // Act & Assert
        Assert.Throws<ModbusConfigurationException>(() => Convert(new ModbusRegisterAttribute(0, ModbusDataType.S64), typeof(ulong?), new byte[8]));
    }

    [Fact]
    public void WhenConvertingNegativeS64IntoDecimal_ThenSignIsKept()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.S64), typeof(decimal?), [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE]);

        // Assert
        Assert.Equal(-2m, value);
    }

    [Fact]
    public void WhenConvertingNegativeS16IntoShortBackedEnum_ThenNegativeMemberIsReturned()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.S16), typeof(Direction?), [0xFF, 0xFE]);

        // Assert
        Assert.Equal(Direction.Reverse, value);
    }

    [Fact]
    public void WhenConvertingNegativeS64IntoLongBackedEnum_ThenNegativeMemberIsReturned()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.S64), typeof(Offset?), [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE]);

        // Assert
        Assert.Equal(Offset.Negative, value);
    }

    [Fact]
    public void WhenConvertingU64AboveLongMaximumIntoUnsignedLongBackedEnum_ThenValueIsNotTruncated()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U64), typeof(Marker?), [0x80, 0, 0, 0, 0, 0, 0, 0x01]);

        // Assert
        Assert.Equal(Marker.High, value);
    }

    [Fact]
    public void WhenScaleIsCombinedWithDynamicScaleFactorIntoDecimal_ThenBothApplyExactly()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U16) { Scale = 0.01, ScaleFactorProperty = "Factor" },
            typeof(decimal?), [0x11, 0xC6], exponent: -2);

        // Assert
        Assert.Equal(0.455m, value);
    }

    [Fact]
    public void WhenScaleIsCombinedWithDynamicScaleFactorIntoDouble_ThenBothApply()
    {
        // Act
        var value = (double?)Convert(new ModbusRegisterAttribute(0, ModbusDataType.U16) { Scale = 0.01, ScaleFactorProperty = "Factor" },
            typeof(double?), [0x11, 0xC6], exponent: -2);

        // Assert
        Assert.NotNull(value);
        Assert.Equal(0.455, value.Value, 10);
    }
}
