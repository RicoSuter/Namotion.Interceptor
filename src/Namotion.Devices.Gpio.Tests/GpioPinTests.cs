using Namotion.Interceptor;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

public class GpioPinTests
{
    [Fact]
    public void GpioPin_InitializesWithDefaults()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();

        // Act
        var pin = new GpioPin(context);

        // Assert
        Assert.Equal(0, pin.PinNumber);
        Assert.Equal(GpioPinMode.Input, pin.Mode);
        Assert.False(pin.Value);
    }

    [Fact]
    public void GpioPin_CanSetProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var pin = new GpioPin(context);

        // Act
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Output;
        pin.Value = true;

        // Assert
        Assert.Equal(17, pin.PinNumber);
        Assert.Equal(GpioPinMode.Output, pin.Mode);
        Assert.True(pin.Value);
    }
}
