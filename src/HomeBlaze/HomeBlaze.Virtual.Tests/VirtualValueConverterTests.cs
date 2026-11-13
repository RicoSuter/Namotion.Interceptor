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

    [Fact]
    public void WhenAFloatIsReadAsNumber_ThenItIsADecimal()
    {
        // Act
        var result = VirtualValueConverter.ToDataType(2.5f, VirtualDataType.Number);

        // Assert
        Assert.Equal(2.5m, result);
    }

    [Theory]
    [MemberData(nameof(ValuesOfTheirDataType))]
    public void WhenAValueAlreadyHasTheDataType_ThenTheSameInstanceIsGiven(object value, VirtualDataType dataType)
    {
        // Act
        var result = VirtualValueConverter.ToDataType(value, dataType);

        // Assert
        Assert.Same(value, result);
    }

    public static TheoryData<object, VirtualDataType> ValuesOfTheirDataType() => new()
    {
        { 5m, VirtualDataType.Number },
        { true, VirtualDataType.Boolean },
        { TimeSpan.FromMinutes(1), VirtualDataType.Duration },
        { new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(1)), VirtualDataType.DateTime }
    };

    [Fact]
    public void WhenALocalDateTimeIsReadAsDateTime_ThenItIsTheSameInstant()
    {
        // Arrange
        var dateTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);

        // Act
        var result = VirtualValueConverter.ToDataType(dateTime, VirtualDataType.DateTime);

        // Assert
        var instant = Assert.IsType<DateTimeOffset>(result);
        Assert.Equal(dateTime.ToUniversalTime(), instant.UtcDateTime);
    }

    [Fact]
    public void WhenADecimalIsWrittenToANullableDouble_ThenItIsConverted()
    {
        // Act
        var isConverted = VirtualValueConverter.TryConvertToTarget(2.5m, typeof(double?), out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.Equal(2.5d, converted);
    }

    [Fact]
    public void WhenADateTimeOffsetIsWrittenToADateTime_ThenItIsTheUtcDateTime()
    {
        // Act
        var isConverted = VirtualValueConverter.TryConvertToTarget(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)), typeof(DateTime), out var converted);

        // Assert
        Assert.True(isConverted);
        var dateTime = Assert.IsType<DateTime>(converted);
        Assert.Equal(new DateTime(2026, 1, 2, 1, 4, 5, DateTimeKind.Utc), dateTime);
        Assert.Equal(DateTimeKind.Utc, dateTime.Kind);
    }

    [Theory]
    [InlineData(2.5)]
    [InlineData(-0.1)]
    public void WhenANumberWithAFractionIsWrittenToAnInteger_ThenItIsRejected(double value)
    {
        // Act
        var isDecimalConverted = VirtualValueConverter.TryConvertToTarget((decimal)value, typeof(int), out _);
        var isDoubleConverted = VirtualValueConverter.TryConvertToTarget(value, typeof(long?), out _);

        // Assert
        Assert.False(isDecimalConverted);
        Assert.False(isDoubleConverted);
    }

    [Theory]
    [InlineData("3")]
    [InlineData(" -1")]
    [InlineData("Monday, 2")]
    [InlineData("Monday, Friday")]
    [InlineData("Wednesday, Thursday")]
    [InlineData("Someday")]
    public void WhenTextIsNoValueOfAnEnum_ThenItIsRejected(string text)
    {
        // Act
        var isConverted = VirtualValueConverter.TryConvertToTarget(text, typeof(DayOfWeek), out _);

        // Assert
        Assert.False(isConverted);
    }

    [Fact]
    public void WhenTextCombinesFlagsOfAnEnum_ThenItIsParsed()
    {
        // Act
        var isConverted = VirtualValueConverter.TryConvertToTarget("ReadOnly, Hidden", typeof(FileAttributes), out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.Equal(FileAttributes.ReadOnly | FileAttributes.Hidden, converted);
    }

    [Fact]
    public void WhenANumberIsWrittenToAFlagsEnumAsText_ThenItIsRejected()
    {
        // Act
        var isConverted = VirtualValueConverter.TryConvertToTarget("3", typeof(FileAttributes), out _);

        // Assert
        Assert.False(isConverted);
    }
}
