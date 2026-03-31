using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Namotion.Devices.MyStrom.Tests;

public class MyStromSwitchTests
{
    private static MyStromSwitch CreateSwitch()
    {
        var httpClientFactory = new TestHttpClientFactory();
        return new MyStromSwitch(httpClientFactory, NullLogger<MyStromSwitch>.Instance);
    }

    [Fact]
    public void WhenCreated_ThenHasDefaultValues()
    {
        // Act
        var subject = CreateSwitch();

        // Assert
        Assert.Equal(string.Empty, subject.Name);
        Assert.Null(subject.HostAddress);
        Assert.True(subject.AllowTurnOff);
        Assert.Equal(TimeSpan.FromSeconds(15), subject.PollingInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), subject.RetryInterval);
    }

    [Fact]
    public void WhenCreated_ThenStatusIsStopped()
    {
        // Act
        var subject = CreateSwitch();

        // Assert
        Assert.Equal(ServiceStatus.Stopped, subject.Status);
        Assert.Null(subject.StatusMessage);
        Assert.False(subject.IsConnected);
    }

    [Fact]
    public void WhenCreated_ThenStatePropertiesAreNull()
    {
        // Act
        var subject = CreateSwitch();

        // Assert
        Assert.Null(subject.IsOn);
        Assert.Null(subject.MeasuredPower);
        Assert.Null(subject.MeasuredEnergyConsumed);
        Assert.Null(subject.Temperature);
        Assert.Null(subject.Uptime);
        Assert.Null(subject.LastUpdated);
    }

    [Fact]
    public void WhenNameIsSet_ThenTitleReturnsName()
    {
        // Arrange
        var subject = CreateSwitch();

        // Act
        subject.Name = "Kitchen Switch";

        // Assert
        Assert.Equal("Kitchen Switch", subject.Title);
    }

    [Fact]
    public void WhenNameIsEmpty_ThenTitleFallsBackToHostAddress()
    {
        // Arrange
        var subject = CreateSwitch();

        // Act
        subject.HostAddress = "192.168.1.59";

        // Assert
        Assert.Equal("192.168.1.59", subject.Title);
    }

    [Fact]
    public void WhenIconName_ThenReturnsPower()
    {
        // Arrange
        var subject = CreateSwitch();

        // Assert
        Assert.Equal("Power", subject.IconName);
    }

    [Fact]
    public void WhenIsOnTrue_ThenIconColorIsSuccess()
    {
        // Arrange
        var subject = CreateSwitch();

        // Act
        subject.IsOn = true;

        // Assert
        Assert.Equal("Success", subject.IconColor);
    }

    [Fact]
    public void WhenIsOnFalse_ThenIconColorIsError()
    {
        // Arrange
        var subject = CreateSwitch();

        // Act
        subject.IsOn = false;

        // Assert
        Assert.Equal("Error", subject.IconColor);
    }

    [Fact]
    public void WhenIsOnNull_ThenIconColorIsNull()
    {
        // Arrange
        var subject = CreateSwitch();

        // Assert
        Assert.Null(subject.IconColor);
    }

    [Fact]
    public void WhenConnectedAndRelayOff_ThenTurnOnIsEnabled()
    {
        // Arrange
        var subject = CreateSwitch();

        // Act
        subject.IsConnected = true;
        subject.IsOn = false;

        // Assert
        Assert.True(subject.TurnOn_IsEnabled);
    }

    [Fact]
    public void WhenDisconnected_ThenTurnOnIsDisabled()
    {
        // Arrange
        var subject = CreateSwitch();

        // Act
        subject.IsConnected = false;
        subject.IsOn = false;

        // Assert
        Assert.False(subject.TurnOn_IsEnabled);
    }

    [Fact]
    public void WhenConnectedAndRelayOnAndAllowTurnOff_ThenTurnOffIsEnabled()
    {
        // Arrange
        var subject = CreateSwitch();

        // Act
        subject.IsConnected = true;
        subject.IsOn = true;
        subject.AllowTurnOff = true;

        // Assert
        Assert.True(subject.TurnOff_IsEnabled);
    }

    [Fact]
    public void WhenAllowTurnOffIsFalse_ThenTurnOffIsDisabled()
    {
        // Arrange
        var subject = CreateSwitch();

        // Act
        subject.IsConnected = true;
        subject.IsOn = true;
        subject.AllowTurnOff = false;

        // Assert
        Assert.False(subject.TurnOff_IsEnabled);
    }

    [Fact]
    public void WhenIsWireless_ThenReturnsTrue()
    {
        // Arrange
        var subject = CreateSwitch();

        // Assert
        Assert.True(subject.IsWireless);
    }

    private class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
