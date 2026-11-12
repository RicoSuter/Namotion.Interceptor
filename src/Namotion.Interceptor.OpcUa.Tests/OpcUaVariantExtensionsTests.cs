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
    [InlineData(0L)]
    [InlineData(3155378975999999999L)]
    public void WhenDateTimeIsMinimumOrMaximum_ThenItRoundTrips(long ticks)
    {
        // Arrange
        var value = new DateTime(ticks, DateTimeKind.Utc);

        // Act
        var roundTripped = OpcUaVariantExtensions.ToVariant(value).ToClrValue();

        // Assert
        Assert.Equal(value, Assert.IsType<DateTime>(roundTripped));
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
}
