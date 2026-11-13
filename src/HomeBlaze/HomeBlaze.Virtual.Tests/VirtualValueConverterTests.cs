namespace HomeBlaze.Virtual.Tests;

public class VirtualValueConverterTests
{
    [Theory]
    [InlineData(2, 2)]
    [InlineData(2.5, 2.5)]
    public void WhenANumberIsReadAsNumber_ThenItIsADecimal(object value, double expected)
    {
        // Act
        var result = VirtualValueConverter.ToDataType(value, VirtualDataType.Number);

        // Assert
        Assert.Equal((decimal)expected, result);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e300)]
    public void WhenADoubleHasNoDecimal_ThenTheNumberIsMissing(double value)
    {
        // Act
        var result = VirtualValueConverter.ToDataType(value, VirtualDataType.Number);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenAnEnumIsReadAsText_ThenItIsItsName()
    {
        // Act
        var result = VirtualValueConverter.ToDataType(DayOfWeek.Monday, VirtualDataType.Text);

        // Assert
        Assert.Equal("Monday", result);
    }

    [Fact]
    public void WhenTextIsReadAsNumber_ThenTheNumberIsMissing()
    {
        // Act
        var result = VirtualValueConverter.ToDataType("5", VirtualDataType.Number);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenADateTimeWithoutKindIsReadAsDateTime_ThenItIsTakenAsUtc()
    {
        // Act
        var result = VirtualValueConverter.ToDataType(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified), VirtualDataType.DateTime);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), result);
    }

    [Fact]
    public void WhenADecimalIsWrittenToAnInteger_ThenItIsConverted()
    {
        // Act
        var isConverted = VirtualValueConverter.TryConvertToTarget(3m, typeof(int), out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.Equal(3, converted);
    }

    [Fact]
    public void WhenTextIsWrittenToAnEnum_ThenItIsParsedByName()
    {
        // Act
        var isConverted = VirtualValueConverter.TryConvertToTarget("friday", typeof(DayOfWeek?), out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.Equal(DayOfWeek.Friday, converted);
    }

    [Fact]
    public void WhenNullIsWrittenToANonNullableValueType_ThenItIsRejected()
    {
        // Act
        var isConverted = VirtualValueConverter.TryConvertToTarget(null, typeof(int), out _);

        // Assert
        Assert.False(isConverted);
    }

    [Fact]
    public void WhenAValueOutOfRangeIsWrittenToAByte_ThenItIsRejected()
    {
        // Act
        var isConverted = VirtualValueConverter.TryConvertToTarget(300m, typeof(byte), out _);

        // Assert
        Assert.False(isConverted);
    }
}
