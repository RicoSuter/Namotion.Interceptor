using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Abstractions.Sensors;

namespace HomeBlaze.Services.Tests;

public class ParameterConverterValueTests
{
    [Theory]
    [InlineData(9.6, typeof(int), 10)]
    [InlineData(2.5, typeof(int), 3)]
    [InlineData(-2.5, typeof(long), -3L)]
    [InlineData(2.5f, typeof(int), 3)]
    [InlineData("High", typeof(NotificationPriority), NotificationPriority.High)]
    [InlineData("1", typeof(NotificationPriority), NotificationPriority.Normal)]
    [InlineData(9.5, typeof(string), "9.5")]
    [InlineData(true, typeof(bool?), true)]
    [InlineData(3, typeof(double), 3.0)]
    public void WhenAValueFitsTheType_ThenItIsConverted(object value, Type type, object expected)
    {
        // Act
        var isConverted = ParameterConverter.TryConvertValue(value, type, out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.Equal(expected, converted);
    }

    [Theory]
    [InlineData("abc", typeof(int))]
    [InlineData(DoorState.Open, typeof(NotificationPriority))]
    [InlineData(1.5, typeof(DateTimeOffset))]
    [InlineData("7", typeof(NotificationPriority))]
    [InlineData("-1", typeof(NotificationPriority))]
    [InlineData(double.NaN, typeof(double))]
    [InlineData(double.PositiveInfinity, typeof(decimal))]
    [InlineData(float.NegativeInfinity, typeof(float))]
    [InlineData(1e300, typeof(float))]
    public void WhenAValueDoesNotFitTheType_ThenItIsNotConverted(object value, Type type)
    {
        // Act
        var isConverted = ParameterConverter.TryConvertValue(value, type, out _);

        // Assert
        Assert.False(isConverted);
    }

    [Fact]
    public void WhenADurationIsConvertedToADuration_ThenItIsKept()
    {
        // Act
        var isConverted = ParameterConverter.TryConvertValue(TimeSpan.FromMinutes(5), typeof(TimeSpan?), out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.Equal(TimeSpan.FromMinutes(5), converted);
    }

    [Fact]
    public void WhenADecimalMidpointIsConvertedToAnInteger_ThenItIsRoundedAwayFromZero()
    {
        // Act
        var isConverted = ParameterConverter.TryConvertValue(0.5m, typeof(int), out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.Equal(1, converted);
    }

    [Fact]
    public void WhenAnInstantIsConvertedToADateTime_ThenItIsTheSameInstantInUtc()
    {
        // Arrange
        var instant = new DateTimeOffset(2026, 11, 6, 13, 0, 0, TimeSpan.FromHours(1));

        // Act
        var isConverted = ParameterConverter.TryConvertValue(instant, typeof(DateTime?), out var converted);

        // Assert
        Assert.True(isConverted);
        var dateTime = Assert.IsType<DateTime>(converted);
        Assert.Equal(new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc), dateTime);
        Assert.Equal(DateTimeKind.Utc, dateTime.Kind);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public void WhenADateTimeInUtcOrWithoutKindIsConvertedToAnInstant_ThenItIsReadAsUtc(DateTimeKind kind)
    {
        // Act
        var isConverted = ParameterConverter.TryConvertValue(new DateTime(2026, 11, 6, 12, 0, 0, kind), typeof(DateTimeOffset), out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero), converted);
        Assert.Equal(TimeSpan.Zero, ((DateTimeOffset)converted!).Offset);
    }

    [Fact]
    public void WhenALocalDateTimeIsConvertedToAnInstant_ThenItKeepsItsLocalOffset()
    {
        // Arrange
        var dateTime = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Local);

        // Act
        var isConverted = ParameterConverter.TryConvertValue(dateTime, typeof(DateTimeOffset), out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.Equal(new DateTimeOffset(dateTime), converted);
    }
}
