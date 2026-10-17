namespace HomeBlaze.History.Import.Influx.Tests;

public class ValueConverterTests
{
    [Fact]
    public void WhenDecimalHasBinaryNoise_ThenItIsRoundedToFifteenDigits()
    {
        // Arrange
        var row = Row(MappingValueType.Decimal);

        // Act
        var converted = ValueConverter.TryConvert(row, "26.700000000000003", out var value);

        // Assert
        Assert.True(converted);
        Assert.Equal(26.7m, value);
    }

    [Fact]
    public void WhenDivisorIsSet_ThenValueIsDivided()
    {
        // Arrange
        var row = Row(MappingValueType.Decimal) with { Divisor = 3600m };

        // Act
        var converted = ValueConverter.TryConvert(row, "59781600", out var value);

        // Assert
        Assert.True(converted);
        Assert.Equal(16606m, value);
    }

    [Fact]
    public void WhenDivisionIsInexact_ThenResultIsRoundedToSixDecimals()
    {
        // Arrange
        var row = Row(MappingValueType.Decimal) with { Divisor = 3600m };

        // Act
        ValueConverter.TryConvert(row, "100", out var value);

        // Assert
        Assert.Equal(0.027778m, value);
    }

    [Fact]
    public void WhenValueIsNotFinite_ThenItIsSkipped()
    {
        // Arrange
        var row = Row(MappingValueType.Decimal);

        // Act
        var converted = ValueConverter.TryConvert(row, "NaN", out _);

        // Assert
        Assert.False(converted);
    }

    [Fact]
    public void WhenBooleanIsParsed_ThenItBecomesBool()
    {
        // Arrange
        var row = Row(MappingValueType.Bool);

        // Act
        ValueConverter.TryConvert(row, "true", out var value);

        // Assert
        Assert.Equal(true, value);
    }

    [Fact]
    public void WhenIntegerTargetGetsFraction_ThenItIsSkipped()
    {
        // Arrange
        var row = Row(MappingValueType.Long);

        // Act
        var integral = ValueConverter.TryConvert(row, "12", out var value);
        var fraction = ValueConverter.TryConvert(row, "12.5", out _);

        // Assert
        Assert.True(integral);
        Assert.Equal(12L, value);
        Assert.False(fraction);
    }

    [Fact]
    public void WhenStringIsMapped_ThenMappedOrFallbackValueIsUsed()
    {
        // Arrange
        var row = Row(MappingValueType.String) with
        {
            Values = new Dictionary<string, string> { ["open"] = "Open" },
            FallbackValue = "Unknown"
        };

        // Act
        ValueConverter.TryConvert(row, "open", out var mapped);
        ValueConverter.TryConvert(row, "stop", out var fallback);

        // Assert
        Assert.Equal("Open", mapped);
        Assert.Equal("Unknown", fallback);
    }

    [Fact]
    public void WhenStringIsUnmappedWithoutFallback_ThenItIsSkipped()
    {
        // Arrange
        var row = Row(MappingValueType.String) with
        {
            Values = new Dictionary<string, string> { ["open"] = "Open" }
        };

        // Act
        var converted = ValueConverter.TryConvert(row, "stop", out _);

        // Assert
        Assert.False(converted);
    }

    private static MappingRow Row(MappingValueType type) =>
        new(2, true, "high", "m", "f", "/a/b", type, 1m, null, null, "");
}
