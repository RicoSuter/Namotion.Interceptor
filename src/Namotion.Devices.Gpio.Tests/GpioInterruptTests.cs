using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Devices.Gpio.Tests.Mocks;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

/// <summary>
/// Integration tests for GPIO interrupt handling and value synchronization.
/// </summary>
public class GpioInterruptTests
{
    [Fact]
    public void InterruptHandler_RisingEdge_UpdatesPinValue()
    {
        // Arrange
        var mockDriver = new MockGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, mockDriver);
        controller.OpenPin(17, PinMode.Input);

        var pins = new Dictionary<int, GpioPin>();

        // Create pin like GpioSubject does
        var pin = new GpioPin
        {
            PinNumber = 17,
            Mode = GpioPinMode.Input,
            Status = ServiceStatus.Running,
            Value = false
        };
        pins[17] = pin;
        
        controller.RegisterCallbackForPinValueChangedEvent(17, PinEventTypes.Rising | PinEventTypes.Falling, (_, args) =>
        {
            if (pins.TryGetValue(args.PinNumber, out var p) && p.Status == ServiceStatus.Running)
            {
                p.Value = args.ChangeType == PinEventTypes.Rising;
            }
        });

        // Act - Simulate hardware pin going high
        mockDriver.SimulatePinValueChange(17, PinValue.High);

        // Assert
        Assert.True(pin.Value);
    }

    [Fact]
    public void InterruptHandler_FallingEdge_UpdatesPinValue()
    {
        // Arrange
        var mockDriver = new MockGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, mockDriver);
        controller.OpenPin(17, PinMode.Input);

        var pins = new Dictionary<int, GpioPin>();

        var pin = new GpioPin
        {
            PinNumber = 17,
            Mode = GpioPinMode.Input,
            Status = ServiceStatus.Running,
            Value = true // Start high
        };
        pins[17] = pin;

        controller.RegisterCallbackForPinValueChangedEvent(17, PinEventTypes.Rising | PinEventTypes.Falling, (_, args) =>
        {
            if (pins.TryGetValue(args.PinNumber, out var p) && p.Status == ServiceStatus.Running)
            {
                p.Value = args.ChangeType == PinEventTypes.Rising;
            }
        });

        // Act - Simulate hardware pin going low
        mockDriver.SimulatePinValueChange(17, PinValue.Low);

        // Assert
        Assert.False(pin.Value);
    }

    [Fact]
    public void InterruptHandler_NotRunningPin_DoesNotUpdateValue()
    {
        // Arrange
        var mockDriver = new MockGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, mockDriver);
        controller.OpenPin(17, PinMode.Input);

        var pins = new Dictionary<int, GpioPin>();

        var pin = new GpioPin
        {
            PinNumber = 17,
            Mode = GpioPinMode.Input,
            Status = ServiceStatus.Unavailable, // Not running
            Value = false
        };
        pins[17] = pin;

        controller.RegisterCallbackForPinValueChangedEvent(17, PinEventTypes.Rising | PinEventTypes.Falling, (_, args) =>
        {
            if (pins.TryGetValue(args.PinNumber, out var p) && p.Status == ServiceStatus.Running)
            {
                p.Value = args.ChangeType == PinEventTypes.Rising;
            }
        });

        // Act
        mockDriver.SimulatePinValueChange(17, PinValue.High);

        // Assert - Value should not change
        Assert.False(pin.Value);
    }

    [Fact]
    public void PollingVerification_DetectsDrift_UpdatesPinValue()
    {
        // Arrange
        var mockDriver = new MockGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, mockDriver);
        controller.OpenPin(17, PinMode.Input);

        var pin = new GpioPin
        {
            PinNumber = 17,
            Mode = GpioPinMode.Input,
            Status = ServiceStatus.Running,
            Value = false
        };

        // Simulate hardware value changing without interrupt (drift scenario)
        mockDriver.SimulatePinValueChange(17, PinValue.High);

        // Act - Polling verification like GpioSubject does
        var actualValue = controller.Read(17) == PinValue.High;
        if (pin.Value != actualValue)
        {
            pin.Value = actualValue;
        }

        // Assert
        Assert.True(pin.Value);
    }

    [Fact]
    public void FullWorkflow_OutputWrite_ReadBack_Verify()
    {
        // Arrange
        var mockDriver = new MockGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, mockDriver);
        controller.OpenPin(17, PinMode.Output);

        var pin = new GpioPin
        {
            Controller = controller,
            PinNumber = 17,
            Mode = GpioPinMode.Output,
            Status = ServiceStatus.Running
        };

        // Act - Write high
        pin.Value = true;

        // Assert
        Assert.Equal(PinValue.High, mockDriver.PinValues[17]);
        Assert.Equal(ServiceStatus.Running, pin.Status); // No error

        // Act - Write low
        pin.Value = false;

        // Assert
        Assert.Equal(PinValue.Low, mockDriver.PinValues[17]);
        Assert.Equal(ServiceStatus.Running, pin.Status);
    }

    [Fact]
    public void FullWorkflow_ModeChange_InterruptManagement()
    {
        // Arrange
        var mockDriver = new MockGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, mockDriver);
        controller.OpenPin(17, PinMode.Input);

        var registeredInterrupts = new HashSet<int>();

        var pin = new GpioPin
        {
            Controller = controller,
            RegisterInterrupt = pin => registeredInterrupts.Add(pin),
            UnregisterInterrupt = pin => registeredInterrupts.Remove(pin),
            PinNumber = 17,
            Mode = GpioPinMode.Input,
            Status = ServiceStatus.Running
        };

        // Register interrupt initially
        registeredInterrupts.Add(17);

        // Act - Change to output
        pin.Mode = GpioPinMode.Output;

        // Assert - Interrupt should be unregistered
        Assert.DoesNotContain(17, registeredInterrupts);
        Assert.Equal(PinMode.Output, mockDriver.PinModes[17]);

        // Act - Change back to input
        pin.Mode = GpioPinMode.Input;

        // Assert - Interrupt should be registered again
        Assert.Contains(17, registeredInterrupts);
        Assert.Equal(PinMode.Input, mockDriver.PinModes[17]);
    }

    [Fact]
    public void MultiplePins_IndependentOperation()
    {
        // Arrange
        var mockDriver = new MockGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, mockDriver);

        var pins = new List<GpioPin>();
        for (int i = 17; i <= 20; i++)
        {
            controller.OpenPin(i, PinMode.Output);
            var pin = new GpioPin
            {
                Controller = controller,
                PinNumber = i,
                Mode = GpioPinMode.Output,
                Status = ServiceStatus.Running
            };
            pins.Add(pin);
        }

        // Act - Set different values
        pins[0].Value = true;  // Pin 17
        pins[1].Value = false; // Pin 18
        pins[2].Value = true;  // Pin 19
        pins[3].Value = false; // Pin 20

        // Assert - Each pin has independent value
        Assert.Equal(PinValue.High, mockDriver.PinValues[17]);
        Assert.Equal(PinValue.Low, mockDriver.PinValues[18]);
        Assert.Equal(PinValue.High, mockDriver.PinValues[19]);
        Assert.Equal(PinValue.Low, mockDriver.PinValues[20]);
    }
}
