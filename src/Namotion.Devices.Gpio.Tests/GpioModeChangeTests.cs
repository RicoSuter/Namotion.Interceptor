using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Devices.Gpio.Simulation;
using Xunit;

using HardwareGpioController = System.Device.Gpio.GpioController;

namespace Namotion.Devices.Gpio.Tests;

public class GpioModeChangeTests
{
    [Fact]
    public void ModeChange_InputToOutput_UnregistersInterruptAndSetsMode()
    {
        // Arrange
        var simulationDriver = new SimulationGpioDriver();
        using var controller = new HardwareGpioController(PinNumberingScheme.Logical, simulationDriver);
        controller.OpenPin(17, PinMode.Input);

        var unregisteredPins = new List<int>();
        var pin = new GpioPin
        {
            Controller = controller,
            UnregisterInterrupt = pin => unregisteredPins.Add(pin),
            PinNumber = 17,
            Mode = GpioPinMode.Input,
            Status = ServiceStatus.Running
        };

        // Act
        pin.Mode = GpioPinMode.Output;

        // Assert
        Assert.Contains(17, unregisteredPins);
        Assert.Equal(PinMode.Output, simulationDriver.PinModes[17]);
    }

    [Fact]
    public void ModeChange_OutputToInput_RegistersInterruptAndReadsValue()
    {
        // Arrange
        var simulationDriver = new SimulationGpioDriver();
        using var controller = new HardwareGpioController(PinNumberingScheme.Logical, simulationDriver);
        controller.OpenPin(17, PinMode.Output);
        var registeredPins = new List<int>();

        var pin = new GpioPin
        {
            Controller = controller,
            RegisterInterrupt = pin => registeredPins.Add(pin),
            PinNumber = 17,
            Mode = GpioPinMode.Output,
            Status = ServiceStatus.Running,
            Value = false
        };

        simulationDriver.SimulatePinValueChange(17, PinValue.High); // Set initial high value

        // Act
        pin.Mode = GpioPinMode.Input;

        // Assert
        Assert.Contains(17, registeredPins);
        Assert.Equal(PinMode.Input, simulationDriver.PinModes[17]);
        Assert.True(pin.Value); // Should have read the high value from hardware
    }

    [Fact]
    public void ModeChange_InputToInputPullUp_SetsHardwareMode()
    {
        // Arrange
        var simulationDriver = new SimulationGpioDriver();
        using var controller = new HardwareGpioController(PinNumberingScheme.Logical, simulationDriver);
        controller.OpenPin(17, PinMode.Input);

        var unregisteredPins = new List<int>();
        var registeredPins = new List<int>();

        var pin = new GpioPin
        {
            Controller = controller,
            RegisterInterrupt = pin => registeredPins.Add(pin),
            UnregisterInterrupt = pin => unregisteredPins.Add(pin),
            PinNumber = 17,
            Mode = GpioPinMode.Input,
            Status = ServiceStatus.Running
        };

        // Act
        pin.Mode = GpioPinMode.InputPullUp;

        // Assert - Should not register/unregister since both are input modes
        Assert.Empty(unregisteredPins);
        Assert.Empty(registeredPins);
        Assert.Equal(PinMode.InputPullUp, simulationDriver.PinModes[17]);
    }

    [Fact]
    public void ModeChange_InputToInputPullDown_SetsHardwareMode()
    {
        // Arrange
        var simulationDriver = new SimulationGpioDriver();
        using var controller = new HardwareGpioController(PinNumberingScheme.Logical, simulationDriver);
        controller.OpenPin(17, PinMode.Input);

        var pin = new GpioPin
        {
            Controller = controller,
            RegisterInterrupt = _ => { },
            UnregisterInterrupt = _ => { },
            PinNumber = 17,
            Mode = GpioPinMode.Input,
            Status = ServiceStatus.Running
        };

        // Act
        pin.Mode = GpioPinMode.InputPullDown;

        // Assert
        Assert.Equal(PinMode.InputPullDown, simulationDriver.PinModes[17]);
    }

    [Fact]
    public void ModeChange_ToOutput_WritesCurrentValueToHardware()
    {
        // Arrange
        var simulationDriver = new SimulationGpioDriver();
        using var controller = new HardwareGpioController(PinNumberingScheme.Logical, simulationDriver);
        controller.OpenPin(17, PinMode.Input);

        var pin = new GpioPin
        {
            Controller = controller,
            RegisterInterrupt = _ => { },
            UnregisterInterrupt = _ => { },
            PinNumber = 17,
            Mode = GpioPinMode.Input,
            Status = ServiceStatus.Running,
            Value = true // Set value before mode change
        };

        // Act
        pin.Mode = GpioPinMode.Output;

        // Assert - Should write the current value (true/High) to hardware
        Assert.Contains(simulationDriver.WriteHistory, w => w.PinNumber == 17 && w.Value == PinValue.High);
    }

    [Fact]
    public void ModeChange_NotRunningStatus_DoesNothing()
    {
        // Arrange
        var simulationDriver = new SimulationGpioDriver();
        using var controller = new HardwareGpioController(PinNumberingScheme.Logical, simulationDriver);
        controller.OpenPin(17, PinMode.Input);

        var interruptCalled = false;

        var pin = new GpioPin
        {
            Controller = controller,
            RegisterInterrupt = _ => interruptCalled = true,
            UnregisterInterrupt = _ => interruptCalled = true,
            PinNumber = 17,
            Mode = GpioPinMode.Input,
            Status = ServiceStatus.Unavailable // Not running
        };

        // Act
        pin.Mode = GpioPinMode.Output;

        // Assert - Mode should change in object but no hardware interaction
        Assert.Equal(GpioPinMode.Output, pin.Mode);
        Assert.False(interruptCalled);
    }

    [Fact]
    public void ModeChange_SameMode_DoesNothing()
    {
        // Arrange
        var simulationDriver = new SimulationGpioDriver();
        using var controller = new HardwareGpioController(PinNumberingScheme.Logical, simulationDriver);
        controller.OpenPin(17, PinMode.Input);

        var interactionCount = 0;

        var pin = new GpioPin
        {
            Controller = controller,
            RegisterInterrupt = _ => interactionCount++,
            UnregisterInterrupt = _ => interactionCount++,
            PinNumber = 17,
            Mode = GpioPinMode.Input,
            Status = ServiceStatus.Running
        };

        // Act - Set to same mode
        pin.Mode = GpioPinMode.Input;

        // Assert - No additional hardware interactions
        Assert.Equal(0, interactionCount);
    }
}
