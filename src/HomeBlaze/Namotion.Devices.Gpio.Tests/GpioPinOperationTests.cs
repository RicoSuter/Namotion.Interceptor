using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Devices.Gpio.Simulation;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

public class GpioPinOperationTests
{
    [Theory]
    [InlineData(GpioPinMode.Input)]
    [InlineData(GpioPinMode.InputPullUp)]
    [InlineData(GpioPinMode.InputPullDown)]
    public async Task WhenPinIsInput_ThenSetValueThrows(GpioPinMode mode)
    {
        // Arrange
        var pin = new GpioPin { PinNumber = 18, Mode = mode };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pin.SetValueAsync(true, CancellationToken.None));
        Assert.Equal("Pin 18 is an input; its value follows the hardware.", exception.Message);
    }

    [Fact]
    public async Task WhenPinIsInput_ThenTurnOnTurnOffAndToggleThrow()
    {
        // Arrange
        var pin = new GpioPin { PinNumber = 18, Mode = GpioPinMode.InputPullUp };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => pin.TurnOnAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pin.TurnOffAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pin.ToggleAsync(CancellationToken.None));
        Assert.False(pin.Value);
    }

    [Fact]
    public async Task WhenRunningOutputIsToggled_ThenValueAndLevelFlip()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        using var controller = new GpioController(driver);
        controller.OpenPin(17, PinMode.Output);
        var pin = new GpioPin { PinNumber = 17, Mode = GpioPinMode.Output, Status = ServiceStatus.Running };
        pin.Controller = controller;

        // Act
        await pin.ToggleAsync(CancellationToken.None);

        // Assert
        Assert.True(pin.Value);
        Assert.Equal(PinValue.High, driver.PinValues[17]);
    }

    [Fact]
    public async Task WhenRunningInvertedOutputIsToggledOff_ThenHighLevelIsWritten()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        using var controller = new GpioController(driver);
        controller.OpenPin(17, PinMode.Output);
        var pin = new GpioPin { PinNumber = 17, Mode = GpioPinMode.Output, IsInverted = true, Status = ServiceStatus.Running };
        pin.Controller = controller;
        await pin.SetValueAsync(true, CancellationToken.None);
        Assert.Equal(PinValue.Low, driver.PinValues[17]);

        // Act
        await pin.ToggleAsync(CancellationToken.None);

        // Assert
        Assert.False(pin.Value);
        Assert.Equal(PinValue.High, driver.PinValues[17]);
    }

    [Fact]
    public async Task WhenOutputIsNotRunning_ThenSetValueOnlyChangesValue()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        using var controller = new GpioController(driver);
        controller.OpenPin(17, PinMode.Output);
        controller.Write(17, PinValue.Low);
        var pin = new GpioPin { PinNumber = 17, Mode = GpioPinMode.Output, Status = ServiceStatus.Error };
        pin.Controller = controller;

        // Act
        await pin.SetValueAsync(true, CancellationToken.None);

        // Assert
        Assert.True(pin.Value);
        Assert.Equal(PinValue.Low, driver.PinValues[17]);
    }

    [Fact]
    public async Task WhenRunningOutputIsSetToTrue_ThenHighLevelIsWritten()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        using var controller = new GpioController(driver);
        controller.OpenPin(17, PinMode.Output);
        var pin = new GpioPin { PinNumber = 17, Mode = GpioPinMode.Output, Status = ServiceStatus.Running };
        pin.Controller = controller;

        // Act
        await pin.SetValueAsync(true, CancellationToken.None);

        // Assert
        Assert.True(pin.Value);
        Assert.Equal(PinValue.High, driver.PinValues[17]);
    }

    [Fact]
    public void WhenPinIsRunningOutput_ThenWriteOperationsAreEnabled()
    {
        // Act
        var pin = new GpioPin { Mode = GpioPinMode.Output, Status = ServiceStatus.Running };

        // Assert
        Assert.True(pin.SetValue_IsEnabled);
        Assert.True(pin.Toggle_IsEnabled);
    }

    [Theory]
    [InlineData(GpioPinMode.Input, ServiceStatus.Running)]
    [InlineData(GpioPinMode.Output, ServiceStatus.Stopped)]
    public void WhenPinIsInputOrNotRunning_ThenWriteOperationsAreDisabled(GpioPinMode mode, ServiceStatus status)
    {
        // Act
        var pin = new GpioPin { Mode = mode, Status = status };

        // Assert
        Assert.False(pin.SetValue_IsEnabled);
        Assert.False(pin.Toggle_IsEnabled);
    }
}
