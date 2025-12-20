using HomeBlaze.Abstractions;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

public class GpioPinTests
{
    [Fact]
    public void GpioPin_InitializesWithDefaults()
    {
        // Act
        var pin = new GpioPin();

        // Assert
        Assert.Equal(0, pin.PinNumber);
        Assert.Equal(GpioPinMode.Input, pin.Mode);
        Assert.False(pin.Value);
    }

    [Fact]
    public void GpioPin_CanSetProperties()
    {
        // Arrange
        var pin = new GpioPin();

        // Act
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Output;
        pin.Value = true;

        // Assert
        Assert.Equal(17, pin.PinNumber);
        Assert.Equal(GpioPinMode.Output, pin.Mode);
        Assert.True(pin.Value);
    }

    [Fact]
    public void GpioPin_InitializesWithStoppedStatus()
    {
        // Act
        var pin = new GpioPin();

        // Assert
        Assert.Equal(ServiceStatus.Stopped, pin.Status);
        Assert.Null(pin.StatusMessage);
    }

    [Fact]
    public void GpioPin_CanSetStatusProperties()
    {
        // Arrange
        var pin = new GpioPin();

        // Act
        pin.Status = ServiceStatus.Running;
        pin.StatusMessage = "Pin is active";

        // Assert
        Assert.Equal(ServiceStatus.Running, pin.Status);
        Assert.Equal("Pin is active", pin.StatusMessage);
    }

    [Theory]
    [InlineData(GpioPinMode.Input)]
    [InlineData(GpioPinMode.InputPullUp)]
    [InlineData(GpioPinMode.InputPullDown)]
    [InlineData(GpioPinMode.Output)]
    public void GpioPin_SupportsAllPinModes(GpioPinMode mode)
    {
        // Arrange
        var pin = new GpioPin();

        // Act
        pin.Mode = mode;

        // Assert
        Assert.Equal(mode, pin.Mode);
    }

    [Fact]
    public void GpioPin_TitleAndIcon_ReturnExpectedValues()
    {
        // Arrange
        var pin = new GpioPin { PinNumber = 17 };

        // Act & Assert
        Assert.Equal("Pin 17", pin.Title);
        Assert.Equal("Settings", pin.Icon);
    }
}
