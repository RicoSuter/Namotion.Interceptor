using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Devices.Gpio.Simulation;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

public class GpioWriteTests
{
    [Fact]
    public void WriteProperty_OutputPin_WritesToHardware()
    {
        // Arrange
        var simulationDriver = new SimulationGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, simulationDriver);
        controller.OpenPin(17, PinMode.Output);

        var pin = new GpioPin
        {
            Controller = controller,
            PinNumber = 17,
            Mode = GpioPinMode.Output,
            Status = ServiceStatus.Running
        };

        // Act
        pin.Value = true;

        // Assert
        Assert.Contains(simulationDriver.WriteHistory, w => w.PinNumber == 17 && w.Value == PinValue.High);
    }

    [Fact]
    public void WriteProperty_OutputPin_WritesLowValue()
    {
        // Arrange
        var simulationDriver = new SimulationGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, simulationDriver);
        controller.OpenPin(17, PinMode.Output);
        simulationDriver.SimulatePinValueChange(17, PinValue.High); // Start high

        var pin = new GpioPin
        {
            Controller = controller,
            PinNumber = 17,
            Mode = GpioPinMode.Output,
            Status = ServiceStatus.Running,
            Value = true
        };

        // Act
        pin.Value = false;

        // Assert
        Assert.Contains(simulationDriver.WriteHistory, w => w.PinNumber == 17 && w.Value == PinValue.Low);
    }

    [Fact]
    public void WriteProperty_InputPin_DoesNotWriteToHardware()
    {
        // Arrange
        var simulationDriver = new SimulationGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, simulationDriver);
        controller.OpenPin(17, PinMode.Input);

        var pin = new GpioPin
        {
            Controller = controller,
            PinNumber = 17,
            Mode = GpioPinMode.Input, // Input mode
            Status = ServiceStatus.Running
        };

        // Act
        pin.Value = true;

        // Assert - No writes should have happened for input pin
        Assert.DoesNotContain(simulationDriver.WriteHistory, w => w.PinNumber == 17);
    }

    [Fact]
    public void WriteProperty_NotRunningStatus_DoesNotWriteToHardware()
    {
        // Arrange
        var simulationDriver = new SimulationGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, simulationDriver);
        controller.OpenPin(17, PinMode.Output);

        var pin = new GpioPin
        {
            Controller = controller,
            PinNumber = 17,
            Mode = GpioPinMode.Output,
            Status = ServiceStatus.Unavailable // Not running
        };

        // Act
        pin.Value = true;

        // Assert - No writes should happen when status is not Running
        Assert.DoesNotContain(simulationDriver.WriteHistory, w => w.PinNumber == 17);
    }

    [Fact]
    public void WriteProperty_NonValueProperty_DoesNotWriteToHardware()
    {
        // Arrange
        var simulationDriver = new SimulationGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, simulationDriver);
        controller.OpenPin(17, PinMode.Output);

        var pin = new GpioPin
        {
            Controller = controller,
            PinNumber = 17,
            Mode = GpioPinMode.Output,
            Status = ServiceStatus.Running
        };

        // Act - Change a non-Value property
        pin.StatusMessage = "Test message";

        // Assert - No writes should happen for non-Value properties
        Assert.Empty(simulationDriver.WriteHistory);
    }

    [Fact]
    public void WriteProperty_VerificationFails_SetsErrorStatus()
    {
        // Arrange
        var simulationDriver = new SimulationGpioDriverWithVerificationFailure();
        using var controller = new GpioController(PinNumberingScheme.Logical, simulationDriver);
        controller.OpenPin(17, PinMode.Output);

        var pin = new GpioPin
        {
            Controller = controller,
            PinNumber = 17,
            Mode = GpioPinMode.Output,
            Status = ServiceStatus.Running
        };

        // Act - Write should fail verification
        pin.Value = true;

        // Assert - Status should be set to Error, value remains as set
        Assert.Equal(ServiceStatus.Error, pin.Status);
        Assert.Contains("verification failed", pin.StatusMessage);
        Assert.True(pin.Value); // Value stays as user set it
    }

    /// <summary>
    /// Mock driver that simulates read-back verification failure.
    /// Always returns opposite of written value on read.
    /// </summary>
    private class SimulationGpioDriverWithVerificationFailure : SimulationGpioDriver
    {
        protected override PinValue Read(int pinNumber)
        {
            // Always return opposite of stored value to fail verification
            var value = base.Read(pinNumber);
            return value == PinValue.High ? PinValue.Low : PinValue.High;
        }
    }
}
