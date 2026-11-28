using HomeBlaze.Abstractions.Devices.Energy;
using HueApi.Models;
using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

public class HueBatteryStateTests
{
    [Theory]
    [InlineData(80, BatteryState.low, true)]
    [InlineData(80, BatteryState.critical, true)]
    [InlineData(5, BatteryState.normal, false)]
    [InlineData(5, null, true)]
    [InlineData(50, null, false)]
    [InlineData(null, null, null)]
    public void WhenTheBridgeReportsTheBatteryOfAButtonDevice_ThenIsBatteryLowPrefersTheBatteryState(int? batteryPercent, BatteryState? batteryState, bool? expected)
    {
        // Arrange
        var devicePower = TestHelpers.CreateDevicePower(batteryPercent, batteryState);

        // Act
        IBatteryState buttonDevice = TestHelpers.CreateButtonDevice([TestHelpers.CreateButtonResource(null)], devicePower: devicePower);

        // Assert
        Assert.Equal(expected, buttonDevice.IsBatteryLow);
    }

    [Theory]
    [InlineData(80, BatteryState.low, true)]
    [InlineData(80, BatteryState.critical, true)]
    [InlineData(5, BatteryState.normal, false)]
    [InlineData(5, null, true)]
    [InlineData(null, null, null)]
    public void WhenTheBridgeReportsTheBatteryOfAMotionDevice_ThenIsBatteryLowPrefersTheBatteryState(int? batteryPercent, BatteryState? batteryState, bool? expected)
    {
        // Arrange
        var devicePower = TestHelpers.CreateDevicePower(batteryPercent, batteryState);

        // Act
        IBatteryState motionDevice = TestHelpers.CreateMotionDevice(devicePower);

        // Assert
        Assert.Equal(expected, motionDevice.IsBatteryLow);
    }
}
