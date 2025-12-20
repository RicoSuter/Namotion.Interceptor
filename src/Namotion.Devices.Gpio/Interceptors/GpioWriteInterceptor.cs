using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Devices.Gpio.Interceptors;

/// <summary>
/// Writes pin value to hardware when GpioPin.Value changes (output mode only).
/// Includes read-back verification.
/// </summary>
public class GpioWriteInterceptor : IWriteInterceptor
{
    private readonly GpioController _controller;

    public GpioWriteInterceptor(GpioController controller)
    {
        _controller = controller;
    }

    public void WriteProperty<T>(ref PropertyWriteContext<T> context, WriteInterceptionDelegate<T> next)
    {
        // Capture the new value before calling next
        var newValue = context.NewValue;

        next(ref context);

        if (context.Property.Subject is not GpioPin pin)
            return;

        if (context.Property.Metadata.Name != nameof(GpioPin.Value))
            return;

        if (pin.Mode != GpioPinMode.Output)
            return;

        if (pin.Status != ServiceStatus.Running)
            return;

        // Write to hardware
        var pinValue = pin.Value ? PinValue.High : PinValue.Low;
        _controller.Write(pin.PinNumber, pinValue);

        // Read-back verification
        var actualValue = _controller.Read(pin.PinNumber) == PinValue.High;
        if (actualValue != pin.Value)
        {
            // Set status first to prevent recursion (status check above will exit early)
            pin.Status = ServiceStatus.Error;
            pin.StatusMessage = "Write verification failed - possible short circuit or external driver";
        }
    }
}
