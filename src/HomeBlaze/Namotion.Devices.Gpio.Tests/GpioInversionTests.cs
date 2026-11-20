using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Devices.Gpio.Simulation;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

public class GpioInversionTests
{
    [Fact]
    public void WhenPinIsCreated_ThenItIsNotInverted()
    {
        // Act
        var pin = new GpioPin();

        // Assert
        Assert.False(pin.IsInverted);
    }

    [Fact]
    public async Task WhenInvertedOutputIsTurnedOn_ThenLowLevelIsWritten()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        using var controller = new GpioController(driver);
        controller.OpenPin(17, PinMode.Output, PinValue.High);
        var pin = CreateRunningPin(controller, GpioPinMode.Output, isInverted: true);

        // Act
        await pin.TurnOnAsync(CancellationToken.None);

        // Assert
        Assert.Equal(PinValue.Low, driver.PinValues[17]);
        Assert.True(pin.IsOn);
        Assert.Equal(ServiceStatus.Running, pin.Status);
    }

    [Fact]
    public async Task WhenInvertedOutputIsTurnedOff_ThenHighLevelIsWritten()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        using var controller = new GpioController(driver);
        controller.OpenPin(17, PinMode.Output, PinValue.High);
        var pin = CreateRunningPin(controller, GpioPinMode.Output, isInverted: true);
        await pin.TurnOnAsync(CancellationToken.None);

        // Act
        await pin.TurnOffAsync(CancellationToken.None);

        // Assert
        Assert.Equal(PinValue.High, driver.PinValues[17]);
        Assert.False(pin.IsOn);
        Assert.Equal(ServiceStatus.Running, pin.Status);
    }

    [Fact]
    public void WhenInvertedInputReadsLowLevel_ThenValueIsTrue()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        using var controller = new GpioController(driver);
        controller.OpenPin(17, PinMode.InputPullUp);
        var pin = CreateRunningPin(controller, GpioPinMode.InputPullUp, isInverted: true);
        pin.DebounceTime = TimeSpan.Zero;

        // Act
        pin.ApplyHardwareLevel(isHigh: false);

        // Assert
        Assert.True(pin.Value);
    }

    [Fact]
    public void WhenInputIsInvertedAtRuntime_ThenValueFollowsTheLevel()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        using var controller = new GpioController(driver);
        controller.OpenPin(17, PinMode.Input);
        driver.SimulatePinValueChange(17, PinValue.Low);
        var pin = CreateRunningPin(controller, GpioPinMode.Input, isInverted: false);

        // Act
        pin.IsInverted = true;

        // Assert
        Assert.True(pin.Value);
    }

    [Fact]
    public void WhenOutputIsInvertedAtRuntime_ThenThePhysicalLevelIsKept()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        using var controller = new GpioController(driver);
        controller.OpenPin(17, PinMode.Output, PinValue.Low);
        var pin = CreateRunningPin(controller, GpioPinMode.Output, isInverted: false);

        // Act
        pin.IsInverted = true;

        // Assert
        Assert.Equal(PinValue.Low, driver.PinValues[17]);
        Assert.True(pin.Value);
        Assert.Equal(ServiceStatus.Running, pin.Status);
    }

    [Fact]
    public void WhenInvertedPinChangesToInput_ThenValueIsTheInvertedLevel()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        using var controller = new GpioController(driver);
        controller.OpenPin(17, PinMode.Output, PinValue.High);
        var pin = CreateRunningPin(controller, GpioPinMode.Output, isInverted: true);
        pin.RegisterInterrupt = _ => { };
        pin.UnregisterInterrupt = _ => { };
        driver.SimulatePinValueChange(17, PinValue.Low);

        // Act
        pin.Mode = GpioPinMode.Input;

        // Assert
        Assert.True(pin.Value);
    }

    [Fact]
    public async Task WhenSubjectStartsWithInvertedOutputThatIsLow_ThenTheLevelIsKeptAndValueIsTrue()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        driver.SimulatePinValueChange(17, PinValue.Low);
        var subject = GpioTestSubject.Create(driver, pins: new Dictionary<int, GpioPin>
        {
            [17] = new() { Mode = GpioPinMode.Output, IsInverted = true }
        });

        // Act
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);

        // Assert
        Assert.Equal(PinValue.Low, driver.PinValues[17]);
        Assert.DoesNotContain(driver.WriteHistory, write => write.PinNumber == 17 && write.Value == PinValue.High);
        Assert.True(subject.Pins[17].Value);
        Assert.True(subject.Pins[17].IsOn);
    }

    [Fact]
    public async Task WhenInvertedInputOfStartedSubjectGoesLow_ThenValueIsTrue()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        var subject = GpioTestSubject.Create(driver, pins: new Dictionary<int, GpioPin>
        {
            [18] = new() { Mode = GpioPinMode.InputPullUp, IsInverted = true, DebounceTime = TimeSpan.Zero }
        });
        driver.SimulatePinValueChange(18, PinValue.High);
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);
        Assert.False(subject.Pins[18].Value);

        // Act
        driver.SimulatePinValueChange(18, PinValue.Low);

        // Assert
        Assert.True(subject.Pins[18].Value);
    }

    private static GpioPin CreateRunningPin(GpioController controller, GpioPinMode mode, bool isInverted)
    {
        var pin = new GpioPin
        {
            PinNumber = 17,
            Mode = mode,
            IsInverted = isInverted,
            Status = ServiceStatus.Running
        };

        pin.Controller = controller;
        pin.Value = (controller.Read(17) == PinValue.High) != isInverted;
        return pin;
    }
}
