namespace HomeBlaze.Virtual.Tests;

public class VirtualValueConverterTests
{
    [Theory]
    [MemberData(nameof(Numbers))]
    public void WhenANumberIsReadAsNumber_ThenItIsADecimal(object value, decimal expected)
    {
        // Act
        var result = VirtualValueConverter.ToDataType(value, VirtualDataType.Number);

        // Assert
        Assert.Equal(expected, result);
    }

    public static TheoryData<object, decimal> Numbers() => new()
    {
        { 2, 2m },
        { 2.5, 2.5m },
        { 2.5f, 2.5m }
    };

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e300)]
    [InlineData("5")]
    public void WhenAValueHasNoDecimal_ThenTheNumberIsMissing(object value)
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
    public void WhenADateTimeWithoutKindIsReadAsDateTime_ThenItIsTakenAsUtc()
    {
        // Act
        var result = VirtualValueConverter.ToDataType(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified), VirtualDataType.DateTime);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), result);
    }

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

    [Theory]
    [MemberData(nameof(ConvertibleValues))]
    public void WhenAValueIsWrittenToATargetItFits_ThenItIsConverted(object value, Type targetType, object expected)
    {
        // Act
        var isConverted = VirtualValueConverter.TryConvertToTarget(value, targetType, out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.Equal(expected, converted);
    }

    public static TheoryData<object, Type, object> ConvertibleValues() => new()
    {
        { 3m, typeof(int), 3 },
        { 2.5m, typeof(double?), 2.5d },
        // Text is parsed by name, ignoring case, and may combine the flags of a flags enum.
        { "friday", typeof(DayOfWeek?), DayOfWeek.Friday },
        { "ReadOnly, Hidden", typeof(FileAttributes), FileAttributes.ReadOnly | FileAttributes.Hidden }
    };

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
    [MemberData(nameof(InconvertibleValues))]
    public void WhenAValueDoesNotFitTheTarget_ThenItIsRejected(object? value, Type targetType)
    {
        // Act
        var isConverted = VirtualValueConverter.TryConvertToTarget(value, targetType, out _);

        // Assert
        Assert.False(isConverted);
    }

    public static TheoryData<object?, Type> InconvertibleValues() => new()
    {
        { null, typeof(int) },
        { 300m, typeof(byte) },
        // A number with a fraction does not fit an integer.
        { 2.5m, typeof(int) },
        { 2.5, typeof(long?) },
        { -0.1m, typeof(int) },
        { -0.1, typeof(long?) },
        // Text that is no name of an enum, such as a number, is rejected.
        { "3", typeof(DayOfWeek) },
        { " -1", typeof(DayOfWeek) },
        { "Monday, 2", typeof(DayOfWeek) },
        { "Monday, Friday", typeof(DayOfWeek) },
        { "Wednesday, Thursday", typeof(DayOfWeek) },
        { "Someday", typeof(DayOfWeek) },
        { "3", typeof(FileAttributes) }
    };
}
