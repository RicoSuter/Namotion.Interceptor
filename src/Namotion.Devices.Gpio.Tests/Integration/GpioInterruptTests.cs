using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Devices.Gpio.Interceptors;
using Namotion.Devices.Gpio.Tests.Mocks;
using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;
using Xunit;

namespace Namotion.Devices.Gpio.Tests.Integration;

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
        var context = InterceptorSubjectContext.Create();

        // Create pin like GpioSubject does
        var pin = new GpioPin();
        ((IInterceptorSubject)pin).Context.AddFallbackContext(context);
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Input;
        pin.Status = ServiceStatus.Running;
        pin.Value = false;
        pins[17] = pin;

        // Register interrupt handler like GpioSubject does
        PinChangeEventHandler handler = (sender, args) =>
        {
            if (pins.TryGetValue(args.PinNumber, out var p) && p.Status == ServiceStatus.Running)
            {
                p.Value = args.ChangeType == PinEventTypes.Rising;
            }
        };
        controller.RegisterCallbackForPinValueChangedEvent(17, PinEventTypes.Rising | PinEventTypes.Falling, handler);

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
        var context = InterceptorSubjectContext.Create();

        var pin = new GpioPin();
        ((IInterceptorSubject)pin).Context.AddFallbackContext(context);
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Input;
        pin.Status = ServiceStatus.Running;
        pin.Value = true; // Start high
        pins[17] = pin;

        PinChangeEventHandler handler = (sender, args) =>
        {
            if (pins.TryGetValue(args.PinNumber, out var p) && p.Status == ServiceStatus.Running)
            {
                p.Value = args.ChangeType == PinEventTypes.Rising;
            }
        };
        controller.RegisterCallbackForPinValueChangedEvent(17, PinEventTypes.Rising | PinEventTypes.Falling, handler);

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
        var context = InterceptorSubjectContext.Create();

        var pin = new GpioPin();
        ((IInterceptorSubject)pin).Context.AddFallbackContext(context);
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Input;
        pin.Status = ServiceStatus.Unavailable; // Not running
        pin.Value = false;
        pins[17] = pin;

        PinChangeEventHandler handler = (sender, args) =>
        {
            if (pins.TryGetValue(args.PinNumber, out var p) && p.Status == ServiceStatus.Running)
            {
                p.Value = args.ChangeType == PinEventTypes.Rising;
            }
        };
        controller.RegisterCallbackForPinValueChangedEvent(17, PinEventTypes.Rising | PinEventTypes.Falling, handler);

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

        var context = InterceptorSubjectContext.Create();
        var pin = new GpioPin();
        ((IInterceptorSubject)pin).Context.AddFallbackContext(context);
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Input;
        pin.Status = ServiceStatus.Running;
        pin.Value = false;

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

        var context = InterceptorSubjectContext.Create();
        context.AddService<IWriteInterceptor>(new GpioWriteInterceptor(controller));

        var pin = new GpioPin();
        ((IInterceptorSubject)pin).Context.AddFallbackContext(context);
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Output;
        pin.Status = ServiceStatus.Running;

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

        var context = InterceptorSubjectContext.Create();
        context.AddService<IWriteInterceptor>(new GpioWriteInterceptor(controller));
        context.AddService<IWriteInterceptor>(new GpioModeChangeInterceptor(
            controller,
            pin => registeredInterrupts.Add(pin),
            pin => registeredInterrupts.Remove(pin)));

        var pin = new GpioPin();
        ((IInterceptorSubject)pin).Context.AddFallbackContext(context);
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Input;
        pin.Status = ServiceStatus.Running;

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

        var context = InterceptorSubjectContext.Create();
        context.AddService<IWriteInterceptor>(new GpioWriteInterceptor(controller));

        var pins = new List<GpioPin>();
        for (int i = 17; i <= 20; i++)
        {
            controller.OpenPin(i, PinMode.Output);
            var pin = new GpioPin();
            ((IInterceptorSubject)pin).Context.AddFallbackContext(context);
            pin.PinNumber = i;
            pin.Mode = GpioPinMode.Output;
            pin.Status = ServiceStatus.Running;
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
