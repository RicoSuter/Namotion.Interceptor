using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Devices.Gpio.Interceptors;
using Namotion.Devices.Gpio.Tests.Mocks;
using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;
using Xunit;

namespace Namotion.Devices.Gpio.Tests.Interceptors;

public class GpioModeChangeInterceptorTests
{
    [Fact]
    public void ModeChange_InputToOutput_UnregistersInterruptAndSetsMode()
    {
        // Arrange
        var mockDriver = new MockGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, mockDriver);
        controller.OpenPin(17, PinMode.Input);

        var unregisteredPins = new List<int>();
        var registeredPins = new List<int>();

        var context = InterceptorSubjectContext.Create();
        context.AddService<IWriteInterceptor>(new GpioModeChangeInterceptor(
            controller,
            pin => registeredPins.Add(pin),
            pin => unregisteredPins.Add(pin)));

        var pin = new GpioPin();
        ((IInterceptorSubject)pin).Context.AddFallbackContext(context);
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Input;
        pin.Status = ServiceStatus.Running;

        // Act
        pin.Mode = GpioPinMode.Output;

        // Assert
        Assert.Contains(17, unregisteredPins);
        Assert.Equal(PinMode.Output, mockDriver.PinModes[17]);
    }

    [Fact]
    public void ModeChange_OutputToInput_RegistersInterruptAndReadsValue()
    {
        // Arrange
        var mockDriver = new MockGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, mockDriver);
        controller.OpenPin(17, PinMode.Output);
        mockDriver.SimulatePinValueChange(17, PinValue.High); // Set initial high value

        var unregisteredPins = new List<int>();
        var registeredPins = new List<int>();

        var context = InterceptorSubjectContext.Create();
        context.AddService<IWriteInterceptor>(new GpioModeChangeInterceptor(
            controller,
            pin => registeredPins.Add(pin),
            pin => unregisteredPins.Add(pin)));

        var pin = new GpioPin();
        ((IInterceptorSubject)pin).Context.AddFallbackContext(context);
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Output;
        pin.Status = ServiceStatus.Running;
        pin.Value = false;

        // Act
        pin.Mode = GpioPinMode.Input;

        // Assert
        Assert.Contains(17, registeredPins);
        Assert.Equal(PinMode.Input, mockDriver.PinModes[17]);
        Assert.True(pin.Value); // Should have read the high value from hardware
    }

    [Fact]
    public void ModeChange_InputToInputPullUp_SetsHardwareMode()
    {
        // Arrange
        var mockDriver = new MockGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, mockDriver);
        controller.OpenPin(17, PinMode.Input);

        var unregisteredPins = new List<int>();
        var registeredPins = new List<int>();

        var context = InterceptorSubjectContext.Create();
        context.AddService<IWriteInterceptor>(new GpioModeChangeInterceptor(
            controller,
            pin => registeredPins.Add(pin),
            pin => unregisteredPins.Add(pin)));

        var pin = new GpioPin();
        ((IInterceptorSubject)pin).Context.AddFallbackContext(context);
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Input;
        pin.Status = ServiceStatus.Running;

        // Act
        pin.Mode = GpioPinMode.InputPullUp;

        // Assert - Should not register/unregister since both are input modes
        Assert.Empty(unregisteredPins);
        Assert.Empty(registeredPins);
        Assert.Equal(PinMode.InputPullUp, mockDriver.PinModes[17]);
    }

    [Fact]
    public void ModeChange_InputToInputPullDown_SetsHardwareMode()
    {
        // Arrange
        var mockDriver = new MockGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, mockDriver);
        controller.OpenPin(17, PinMode.Input);

        var context = InterceptorSubjectContext.Create();
        context.AddService<IWriteInterceptor>(new GpioModeChangeInterceptor(
            controller,
            _ => { },
            _ => { }));

        var pin = new GpioPin();
        ((IInterceptorSubject)pin).Context.AddFallbackContext(context);
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Input;
        pin.Status = ServiceStatus.Running;

        // Act
        pin.Mode = GpioPinMode.InputPullDown;

        // Assert
        Assert.Equal(PinMode.InputPullDown, mockDriver.PinModes[17]);
    }

    [Fact]
    public void ModeChange_ToOutput_WritesCurrentValueToHardware()
    {
        // Arrange
        var mockDriver = new MockGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, mockDriver);
        controller.OpenPin(17, PinMode.Input);

        var context = InterceptorSubjectContext.Create();
        context.AddService<IWriteInterceptor>(new GpioModeChangeInterceptor(
            controller,
            _ => { },
            _ => { }));

        var pin = new GpioPin();
        ((IInterceptorSubject)pin).Context.AddFallbackContext(context);
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Input;
        pin.Status = ServiceStatus.Running;
        pin.Value = true; // Set value before mode change

        // Act
        pin.Mode = GpioPinMode.Output;

        // Assert - Should write the current value (true/High) to hardware
        Assert.Contains(mockDriver.WriteHistory, w => w.PinNumber == 17 && w.Value == PinValue.High);
    }

    [Fact]
    public void ModeChange_NotRunningStatus_DoesNothing()
    {
        // Arrange
        var mockDriver = new MockGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, mockDriver);
        controller.OpenPin(17, PinMode.Input);

        var interruptCalled = false;

        var context = InterceptorSubjectContext.Create();
        context.AddService<IWriteInterceptor>(new GpioModeChangeInterceptor(
            controller,
            _ => interruptCalled = true,
            _ => interruptCalled = true));

        var pin = new GpioPin();
        ((IInterceptorSubject)pin).Context.AddFallbackContext(context);
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Input;
        pin.Status = ServiceStatus.Unavailable; // Not running

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
        var mockDriver = new MockGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, mockDriver);
        controller.OpenPin(17, PinMode.Input);

        var interactionCount = 0;

        var context = InterceptorSubjectContext.Create();
        context.AddService<IWriteInterceptor>(new GpioModeChangeInterceptor(
            controller,
            _ => interactionCount++,
            _ => interactionCount++));

        var pin = new GpioPin();
        ((IInterceptorSubject)pin).Context.AddFallbackContext(context);
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Input;
        pin.Status = ServiceStatus.Running;

        var initialModeCount = mockDriver.PinModes.Count;

        // Act - Set to same mode
        pin.Mode = GpioPinMode.Input;

        // Assert - No additional hardware interactions
        Assert.Equal(0, interactionCount);
    }
}
