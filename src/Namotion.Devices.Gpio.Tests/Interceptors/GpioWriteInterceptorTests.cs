using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Devices.Gpio.Interceptors;
using Namotion.Devices.Gpio.Tests.Mocks;
using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;
using Xunit;

namespace Namotion.Devices.Gpio.Tests.Interceptors;

public class GpioWriteInterceptorTests
{
    [Fact]
    public void WriteProperty_OutputPin_WritesToHardware()
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

        // Act
        pin.Value = true;

        // Assert
        Assert.Contains(mockDriver.WriteHistory, w => w.PinNumber == 17 && w.Value == PinValue.High);
    }

    [Fact]
    public void WriteProperty_OutputPin_WritesLowValue()
    {
        // Arrange
        var mockDriver = new MockGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, mockDriver);
        controller.OpenPin(17, PinMode.Output);
        mockDriver.SimulatePinValueChange(17, PinValue.High); // Start high

        var context = InterceptorSubjectContext.Create();
        context.AddService<IWriteInterceptor>(new GpioWriteInterceptor(controller));

        var pin = new GpioPin();
        ((IInterceptorSubject)pin).Context.AddFallbackContext(context);
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Output;
        pin.Status = ServiceStatus.Running;
        pin.Value = true;

        // Act
        pin.Value = false;

        // Assert
        Assert.Contains(mockDriver.WriteHistory, w => w.PinNumber == 17 && w.Value == PinValue.Low);
    }

    [Fact]
    public void WriteProperty_InputPin_DoesNotWriteToHardware()
    {
        // Arrange
        var mockDriver = new MockGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, mockDriver);
        controller.OpenPin(17, PinMode.Input);

        var context = InterceptorSubjectContext.Create();
        context.AddService<IWriteInterceptor>(new GpioWriteInterceptor(controller));

        var pin = new GpioPin();
        ((IInterceptorSubject)pin).Context.AddFallbackContext(context);
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Input; // Input mode
        pin.Status = ServiceStatus.Running;

        // Act
        pin.Value = true;

        // Assert - No writes should have happened for input pin
        Assert.DoesNotContain(mockDriver.WriteHistory, w => w.PinNumber == 17);
    }

    [Fact]
    public void WriteProperty_NotRunningStatus_DoesNotWriteToHardware()
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
        pin.Status = ServiceStatus.Unavailable; // Not running

        // Act
        pin.Value = true;

        // Assert - No writes should happen when status is not Running
        Assert.DoesNotContain(mockDriver.WriteHistory, w => w.PinNumber == 17);
    }

    [Fact]
    public void WriteProperty_NonValueProperty_DoesNotWriteToHardware()
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

        // Act - Change a non-Value property
        pin.StatusMessage = "Test message";

        // Assert - No writes should happen for non-Value properties
        Assert.Empty(mockDriver.WriteHistory);
    }

    [Fact]
    public void WriteProperty_VerificationFails_SetsErrorStatus()
    {
        // Arrange
        var mockDriver = new MockGpioDriverWithVerificationFailure();
        using var controller = new GpioController(PinNumberingScheme.Logical, mockDriver);
        controller.OpenPin(17, PinMode.Output);

        var context = InterceptorSubjectContext.Create();
        context.AddService<IWriteInterceptor>(new GpioWriteInterceptor(controller));

        var pin = new GpioPin();
        ((IInterceptorSubject)pin).Context.AddFallbackContext(context);
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Output;
        pin.Status = ServiceStatus.Running;

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
    private class MockGpioDriverWithVerificationFailure : MockGpioDriver
    {
        protected override PinValue Read(int pinNumber)
        {
            // Always return opposite of stored value to fail verification
            var value = base.Read(pinNumber);
            return value == PinValue.High ? PinValue.Low : PinValue.High;
        }
    }
}
