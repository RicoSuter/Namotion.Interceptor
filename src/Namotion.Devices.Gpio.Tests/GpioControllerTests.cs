using HomeBlaze.Abstractions;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

public class GpioControllerTests
{
    [Fact]
    public void GpioController_InitializesWithEmptyCollections()
    {
        // Act
        var controller = new GpioController();

        // Assert
        Assert.NotNull(controller.Pins);
        Assert.Empty(controller.Pins);
        Assert.NotNull(controller.AnalogChannels);
        Assert.Empty(controller.AnalogChannels);
        Assert.Null(controller.Mcp3008);
        Assert.Null(controller.Ads1115);
    }

    [Fact]
    public void GpioController_TitleAndIcon_ReturnExpectedValues()
    {
        // Arrange
        var controller = new GpioController();

        // Act & Assert
        Assert.Equal("GPIO", controller.Title);
        Assert.Equal("Memory", controller.IconName);
    }

    [Fact]
    public void GpioController_InitializesWithStoppedStatus()
    {
        // Act
        var controller = new GpioController();

        // Assert
        Assert.Equal(ServiceStatus.Stopped, controller.Status);
        Assert.Null(controller.StatusMessage);
    }

    [Fact]
    public void GpioController_HasDefaultPollingInterval()
    {
        // Act
        var controller = new GpioController();

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(5), controller.PollingInterval);
    }

    [Fact]
    public void GpioController_Dispose_SetsStoppedStatus()
    {
        // Arrange
        var controller = new GpioController();

        // Act
        controller.Dispose();

        // Assert
        Assert.Equal(ServiceStatus.Stopped, controller.Status);
        Assert.Null(controller.StatusMessage);
    }
}
