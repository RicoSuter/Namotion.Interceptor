using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Host.Services.Display;

namespace HomeBlaze.Host.Services.Tests.Display;

public class StateUnitExtensionsTests
{
    [Theory]
    [InlineData(StateUnit.Watt, 500, "500 W")]
    [InlineData(StateUnit.Watt, 1500, "1.5 kW")]
    [InlineData(StateUnit.Watt, 1000, "1 kW")]
    [InlineData(StateUnit.Watt, 1234, "1.23 kW")]
    [InlineData(StateUnit.Kilowatt, 0.5, "500 W")]
    [InlineData(StateUnit.Kilowatt, 0.001, "1 W")]
    [InlineData(StateUnit.Kilowatt, 5, "5 kW")]
    [InlineData(StateUnit.WattHour, 10500, "10.5 kWh")]
    [InlineData(StateUnit.WattHour, 500, "500 Wh")]
    [InlineData(StateUnit.KilowattHour, 0.8, "800 Wh")]
    [InlineData(StateUnit.KilowattHour, 5, "5 kWh")]
    [InlineData(StateUnit.Meter, 1500, "1.5 km")]
    [InlineData(StateUnit.Meter, 0.5, "500 mm")]
    [InlineData(StateUnit.Meter, 50, "50 m")]
    [InlineData(StateUnit.Millimeter, 1500, "1.5 m")]
    [InlineData(StateUnit.Millimeter, 500, "500 mm")]
    [InlineData(StateUnit.Kilometer, 0.3, "300 m")]
    [InlineData(StateUnit.Kilometer, 5, "5 km")]
    [InlineData(StateUnit.Ampere, 5, "5 A")]
    [InlineData(StateUnit.Ampere, 0.5, "500 mA")]
    [InlineData(StateUnit.Milliampere, 1500, "1.5 A")]
    [InlineData(StateUnit.Milliampere, 500, "500 mA")]
    [InlineData(StateUnit.Kilobyte, 1500, "1.5 MB")]
    [InlineData(StateUnit.Kilobyte, 500, "500 kB")]
    [InlineData(StateUnit.KilobytePerSecond, 1500, "1.5 MB/s")]
    [InlineData(StateUnit.KilobytePerSecond, 500, "500 kB/s")]
    [InlineData(StateUnit.Volt, 230, "230 V")]
    [InlineData(StateUnit.DegreeCelsius, 23.5, "23.5°C")]
    public void WhenFormatWithUnit_ThenAutoScalesCorrectly(StateUnit unit, double value, string expected)
    {
        // Act
        var result = StateUnitExtensions.FormatWithUnit(Convert.ToDecimal(value), unit);

        // Assert
        Assert.Equal(expected, result);
    }
}
