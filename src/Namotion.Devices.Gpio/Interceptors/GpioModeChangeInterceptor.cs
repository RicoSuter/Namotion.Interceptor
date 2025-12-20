using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Devices.Gpio.Interceptors;

/// <summary>
/// Handles pin mode changes: reconfigures hardware, manages interrupts.
/// </summary>
public class GpioModeChangeInterceptor : IWriteInterceptor
{
    private readonly GpioController _controller;
    private readonly Action<int> _registerInterrupt;
    private readonly Action<int> _unregisterInterrupt;

    public GpioModeChangeInterceptor(
        GpioController controller,
        Action<int> registerInterrupt,
        Action<int> unregisterInterrupt)
    {
        _controller = controller;
        _registerInterrupt = registerInterrupt;
        _unregisterInterrupt = unregisterInterrupt;
    }

    public void WriteProperty<T>(ref PropertyWriteContext<T> context, WriteInterceptionDelegate<T> next)
    {
        var oldMode = context.Property.Subject is GpioPin pin ? pin.Mode : default;

        next(ref context);

        if (context.Property.Subject is not GpioPin changedPin)
            return;

        if (context.Property.Metadata.Name != nameof(GpioPin.Mode))
            return;

        if (changedPin.Status != ServiceStatus.Running)
            return;

        var newMode = changedPin.Mode;
        if (oldMode == newMode)
            return;

        // Determine if old mode was input-like
        var wasInput = oldMode is GpioPinMode.Input or GpioPinMode.InputPullUp or GpioPinMode.InputPullDown;
        var isInput = newMode is GpioPinMode.Input or GpioPinMode.InputPullUp or GpioPinMode.InputPullDown;

        // Unregister interrupt if switching from input to output
        if (wasInput && !isInput)
        {
            _unregisterInterrupt(changedPin.PinNumber);
        }

        // Set hardware pin mode
        var hardwareMode = newMode switch
        {
            GpioPinMode.Input => PinMode.Input,
            GpioPinMode.InputPullUp => PinMode.InputPullUp,
            GpioPinMode.InputPullDown => PinMode.InputPullDown,
            GpioPinMode.Output => PinMode.Output,
            _ => PinMode.Input
        };
        _controller.SetPinMode(changedPin.PinNumber, hardwareMode);

        if (isInput)
        {
            // Read current value and register interrupt
            changedPin.Value = _controller.Read(changedPin.PinNumber) == PinValue.High;
            if (!wasInput)
            {
                _registerInterrupt(changedPin.PinNumber);
            }
        }
        else
        {
            // Write current Value to hardware for output mode
            _controller.Write(changedPin.PinNumber, changedPin.Value ? PinValue.High : PinValue.Low);
        }
    }
}
