using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Abstractions.Sensors;
using HomeBlaze.Automation.Runtime;

namespace HomeBlaze.Automation.Tests.Runtime;

public class AutomationValueConverterTests
{
    [Theory]
    [InlineData(9.6, typeof(int), 10)]
    [InlineData("High", typeof(NotificationPriority), NotificationPriority.High)]
    [InlineData(9.5, typeof(string), "9.5")]
    [InlineData(true, typeof(bool?), true)]
    [InlineData(3, typeof(double), 3.0)]
    public void WhenAValueFitsTheType_ThenItIsConverted(object value, Type type, object expected)
    {
        // Act
        var isConverted = AutomationValueConverter.TryConvert(value, type, out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.Equal(expected, converted);
    }

    [Theory]
    [InlineData("abc", typeof(int))]
    [InlineData(DoorState.Open, typeof(NotificationPriority))]
    [InlineData(1.5, typeof(DateTimeOffset))]
    public void WhenAValueDoesNotFitTheType_ThenItIsNotConverted(object value, Type type)
    {
        // Act
        var isConverted = AutomationValueConverter.TryConvert(value, type, out _);

        // Assert
        Assert.False(isConverted);
    }

    [Fact]
    public void WhenADurationIsConvertedToADuration_ThenItIsKept()
    {
        // Act
        var isConverted = AutomationValueConverter.TryConvert(TimeSpan.FromMinutes(5), typeof(TimeSpan?), out var converted);

        // Assert
        Assert.True(isConverted);
        Assert.Equal(TimeSpan.FromMinutes(5), converted);
    }
}
