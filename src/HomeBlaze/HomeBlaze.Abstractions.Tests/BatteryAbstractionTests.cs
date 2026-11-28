using HomeBlaze.Abstractions.Devices.Energy;

namespace HomeBlaze.Abstractions.Tests;

public class BatteryAbstractionTests
{
    [Theory]
    [InlineData(0.05, true)]
    [InlineData(0.1, true)]
    [InlineData(0.11, false)]
    [InlineData(1.0, false)]
    [InlineData(null, null)]
    public void WhenBatteryLevelIsSet_ThenIsBatteryLowFollowsTheDefaultThreshold(double? batteryLevel, bool? expected)
    {
        // Arrange
        IBatteryState battery = new TestBattery { BatteryLevel = (decimal?)batteryLevel };

        // Act
        var isBatteryLow = battery.IsBatteryLow;

        // Assert
        Assert.Equal(expected, isBatteryLow);
    }

    [Fact]
    public void WhenDeviceReportsALowFlag_ThenItOverridesTheDefault()
    {
        // Arrange
        IBatteryState battery = new TestBatteryWithLowFlag { BatteryLevel = null, IsBatteryLow = true };

        // Act
        var isBatteryLow = battery.IsBatteryLow;

        // Assert
        Assert.True(isBatteryLow);
    }

    private sealed class TestBattery : IBatteryState
    {
        public decimal? BatteryLevel { get; init; }
    }

    private sealed class TestBatteryWithLowFlag : IBatteryState
    {
        public decimal? BatteryLevel { get; init; }

        public bool? IsBatteryLow { get; init; }
    }
}
