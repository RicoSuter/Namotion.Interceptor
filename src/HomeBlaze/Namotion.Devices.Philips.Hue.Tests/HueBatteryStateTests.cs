using System.ComponentModel;
using HomeBlaze.Abstractions.Devices.Energy;
using HomeBlaze.Services;
using HueApi.Models;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
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

    [Fact]
    public void WhenAButtonDeviceIsRegistered_ThenIsBatteryLowIsADiscreteDerivedStateAtTheContractPosition()
    {
        // Arrange
        var buttonDevice = TestHelpers.CreateButtonDevice([TestHelpers.CreateButtonResource(null)]);
        AddRegistry(buttonDevice);

        // Act
        var property = buttonDevice.TryGetRegisteredSubject()!.TryGetProperty(nameof(HueButtonDevice.IsBatteryLow))!;

        // Assert
        var metadata = property.GetStateMetadata();
        Assert.True(property.Reference.Metadata.IsDerived);
        Assert.NotNull(metadata);
        Assert.True(metadata.IsDiscrete);
        Assert.Equal(344, metadata.Position);
    }

    [Fact]
    public void WhenTheBridgeReportsALowBattery_ThenIsBatteryLowRaisesPropertyChanged()
    {
        // Arrange
        var buttonDevice = TestHelpers.CreateButtonDevice(
            [TestHelpers.CreateButtonResource(null)], devicePower: TestHelpers.CreateDevicePower(80, BatteryState.normal));
        AddRegistry(buttonDevice);
        Assert.False(buttonDevice.IsBatteryLow);
        var changedProperties = new List<string>();
        ((INotifyPropertyChanged)buttonDevice).PropertyChanged += (_, arguments) => changedProperties.Add(arguments.PropertyName!);

        // Act
        buttonDevice.DevicePowerResource = TestHelpers.CreateDevicePower(80, BatteryState.low);

        // Assert
        Assert.True(buttonDevice.IsBatteryLow);
        Assert.Contains(nameof(HueButtonDevice.IsBatteryLow), changedProperties);
    }

    private static void AddRegistry(IInterceptorSubject subject)
    {
        subject.Context.AddFallbackContext(SubjectContextFactory.Create(new ServiceCollection()));
    }
}
