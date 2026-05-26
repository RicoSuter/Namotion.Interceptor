using HueApi.Models;
using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

public class HueDeviceConnectionTests
{
    [Fact]
    public void WhenZigbeeConnected_ThenIsConnectedTrue()
    {
        // Arrange & Act
        var device = TestHelpers.CreateHueDevice(ConnectivityStatus.connected);

        // Assert
        Assert.True(device.IsConnected);
    }

    [Fact]
    public void WhenZigbeeDisconnected_ThenIsConnectedFalse()
    {
        // Arrange & Act
        var device = TestHelpers.CreateHueDevice(ConnectivityStatus.connectivity_issue);

        // Assert
        Assert.False(device.IsConnected);
    }

    [Fact]
    public void WhenZigbeeNull_ThenIsConnectedTrue()
    {
        // Arrange & Act
        var device = TestHelpers.CreateHueDevice(connectivityStatus: null);

        // Assert
        Assert.True(device.IsConnected);
    }

    [Fact]
    public void WhenDeviceHasName_ThenTitleReturnsName()
    {
        // Arrange & Act
        var device = TestHelpers.CreateHueDevice(name: "Kitchen Light");

        // Assert
        Assert.Equal("Kitchen Light", device.Title);
    }

    [Fact]
    public void WhenDeviceConnected_ThenStatusIsRunning()
    {
        // Arrange & Act
        var device = TestHelpers.CreateHueDevice(ConnectivityStatus.connected);

        // Assert
        Assert.Equal(HomeBlaze.Abstractions.ServiceStatus.Running, device.Status);
    }

    [Fact]
    public void WhenDeviceDisconnected_ThenStatusIsError()
    {
        // Arrange & Act
        var device = TestHelpers.CreateHueDevice(ConnectivityStatus.connectivity_issue);

        // Assert
        Assert.Equal(HomeBlaze.Abstractions.ServiceStatus.Error, device.Status);
    }
}
