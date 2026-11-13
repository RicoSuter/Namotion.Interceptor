using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests;

public class OpcUaVariantExtensionsTests
{
    [Fact]
    public void WhenValueIsEnum_ThenVariantIsItsNumericValue()
    {
        // Act
        var converted = OpcUaVariantExtensions.TryToVariant(DayOfWeek.Wednesday, out var variant);

        // Assert
        Assert.True(converted);
        Assert.Equal(BuiltInType.Enumeration, variant.TypeInfo.BuiltInType);
        Assert.Equal((int)DayOfWeek.Wednesday, Convert.ToInt32(variant.ToClrValue()));
    }

    [Fact]
    public void WhenValueIsEnumArray_ThenVariantIsInt32Array()
    {
        // Act
        var converted = OpcUaVariantExtensions.TryToVariant(new[] { DayOfWeek.Monday, DayOfWeek.Sunday }, out var variant);

        // Assert
        Assert.True(converted);
        Assert.Equal(new[] { 1, 0 }, Assert.IsType<int[]>(variant.ToClrValue()));
    }

    [Fact]
    public void WhenValueIsByteArray_ThenVariantIsByteString()
    {
        // Act
        var variant = OpcUaVariantExtensions.ToVariant(new byte[] { 1, 2, 3 });

        // Assert
        Assert.Equal(BuiltInType.ByteString, variant.TypeInfo.BuiltInType);
        Assert.True(variant.TypeInfo.IsScalar);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(variant.ToClrValue()));
    }

    [Fact]
    public void WhenValueIsSByteArray_ThenVariantStaysAnSByteArray()
    {
        // Act
        var variant = OpcUaVariantExtensions.ToVariant(new sbyte[] { -1, 2 });

        // Assert
        Assert.Equal(BuiltInType.SByte, variant.TypeInfo.BuiltInType);
        Assert.Equal(new sbyte[] { -1, 2 }, Assert.IsType<sbyte[]>(variant.ToClrValue()));
    }

    [Theory]
    [InlineData(0L, DateTimeKind.Utc)]
    [InlineData(3155378975999999999L, DateTimeKind.Utc)]
    [InlineData(0L, DateTimeKind.Unspecified)]
    [InlineData(3155378975999999999L, DateTimeKind.Unspecified)]
    public void WhenDateTimeIsMinimumOrMaximum_ThenItRoundTrips(long ticks, DateTimeKind kind)
    {
        // Arrange
        var value = new DateTime(ticks, kind);

        // Act
        var roundTripped = OpcUaVariantExtensions.ToVariant(value).ToClrValue();

        // Assert
        Assert.Equal(value.Ticks, Assert.IsType<DateTime>(roundTripped).Ticks);
    }

    [Fact]
    public void WhenDateTimeArrayContainsMaximum_ThenItRoundTrips()
    {
        // Arrange
        var value = new[] { DateTime.MaxValue, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc) };

        // Act
        var roundTripped = OpcUaVariantExtensions.ToVariant(value).ToClrValue();

        // Assert
        Assert.Equal(value, Assert.IsType<DateTime[]>(roundTripped));
    }

    [Fact]
    public void WhenEnumValueIsOutsideInt32Range_ThenItHasNoOpcUaRepresentation()
    {
        // Act
        var convertedLong = OpcUaVariantExtensions.TryToVariant(LongEnum.Large, out _);
        var convertedUnsigned = OpcUaVariantExtensions.TryToVariant(UnsignedLongEnum.Large, out _);
        var convertedArray = OpcUaVariantExtensions.TryToVariant(new[] { LongEnum.Small, LongEnum.Large }, out _);

        // Assert
        Assert.False(convertedLong);
        Assert.False(convertedUnsigned);
        Assert.False(convertedArray);
    }

    [Fact]
    public void WhenEnumValueWithWideUnderlyingTypeFitsInt32_ThenVariantIsItsNumericValue()
    {
        // Act
        var variant = OpcUaVariantExtensions.ToVariant(new[] { LongEnum.Small, LongEnum.Negative });

        // Assert
        Assert.Equal(new[] { 1, -1 }, Assert.IsType<int[]>(variant.ToClrValue()));
    }

    [Fact]
    public void WhenUnsignedEnumValueHasHighestBitSet_ThenItRoundTripsWithItsBits()
    {
        // Act
        var scalar = OpcUaVariantExtensions.ToVariant(UnsignedEnum.High).ToClrValue();
        var array = OpcUaVariantExtensions.ToVariant(new[] { UnsignedEnum.Low, UnsignedEnum.High }).ToClrValue();

        // Assert
        Assert.Equal(UnsignedEnum.High, scalar);
        Assert.Equal(new[] { 1, int.MinValue }, Assert.IsType<int[]>(array));
    }

    [Fact]
    public void WhenEnumHasNarrowSignedUnderlyingType_ThenNegativeValueKeepsItsSign()
    {
        // Act
        var array = OpcUaVariantExtensions.ToVariant(new[] { SignedByteEnum.Negative }).ToClrValue();

        // Assert
        Assert.Equal(new[] { -5 }, Assert.IsType<int[]>(array));
    }

    [Fact]
    public void WhenTimestampIsUnset_ThenClrTimestampIsMinValue()
    {
        // Act
        var timestamp = DateTimeUtc.MinValue.ToClrTimestamp();

        // Assert
        Assert.Equal(DateTimeOffset.MinValue, timestamp);
    }

    [Fact]
    public void WhenValueHasNoOpcUaRepresentation_ThenTryToVariantReturnsFalseAndToVariantThrows()
    {
        // Arrange
        object value = DateTimeOffset.UtcNow;

        // Act
        var converted = OpcUaVariantExtensions.TryToVariant(value, out _);

        // Assert
        Assert.False(converted);
        var exception = Assert.Throws<ServiceResultException>(() => OpcUaVariantExtensions.ToVariant(value));
        Assert.Equal(StatusCodes.BadTypeMismatch, exception.StatusCode);
    }

    [Fact]
    public void WhenValueIsNull_ThenVariantIsNull()
    {
        // Act
        var converted = OpcUaVariantExtensions.TryToVariant(null, out var variant);

        // Assert
        Assert.True(converted);
        Assert.Null(variant.ToClrValue());
    }

    private enum LongEnum : long
    {
        Negative = -1,
        Small = 1,
        Large = 1L << 40
    }

    private enum UnsignedLongEnum : ulong
    {
        Large = ulong.MaxValue
    }

    [Flags]
    private enum UnsignedEnum : uint
    {
        Low = 1,
        High = 0x80000000
    }

    private enum SignedByteEnum : sbyte
    {
        Negative = -5
    }
}
