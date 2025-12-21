using System.Device.Gpio;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Gpio;

/// <summary>
/// Represents a single GPIO pin with mode, value, and availability status.
/// </summary>
[InterceptorSubject]
public partial class GpioPin : IHostedSubject, ITitleProvider, IIconProvider
{
    internal GpioController? Controller { get; init; }
    internal Action<int>? RegisterInterrupt { get; init; }
    internal Action<int>? UnregisterInterrupt { get; init; }

    /// <summary>
    /// The GPIO pin number (BCM numbering).
    /// </summary>
    public partial int PinNumber { get; set; }

    /// <summary>
    /// The pin operating mode.
    /// </summary>
    [Configuration]
    public partial GpioPinMode Mode { get; set; }

    /// <summary>
    /// The current pin value (true = high, false = low).
    /// </summary>
    [State]
    public partial bool Value { get; set; }

    /// <summary>
    /// Pin availability status.
    /// </summary>
    [State]
    public partial ServiceStatus Status { get; set; }

    /// <summary>
    /// Status message (e.g., "Reserved for I2C", "Write verification failed").
    /// </summary>
    [State]
    public partial string? StatusMessage { get; set; }

    /// <inheritdoc />
    public string? Title => $"Pin {PinNumber}";

    /// <inheritdoc />
    public string? Icon => "Settings";

    public GpioPin()
    {
        PinNumber = 0;
        Mode = GpioPinMode.Input;
        Value = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    /// <summary>
    /// Called before the Mode property is set. Handles hardware pin mode changes.
    /// </summary>
    partial void OnSetMode(ref GpioPinMode value)
    {
        if (Controller == null || Status != ServiceStatus.Running)
            return;

        var oldMode = Mode;
        var newMode = value;

        if (oldMode == newMode)
            return;

        // Determine if old mode was input-like
        var wasInput = oldMode is GpioPinMode.Input or GpioPinMode.InputPullUp or GpioPinMode.InputPullDown;
        var isInput = newMode is GpioPinMode.Input or GpioPinMode.InputPullUp or GpioPinMode.InputPullDown;

        // Unregister interrupt if switching from input to output
        if (wasInput && !isInput)
        {
            UnregisterInterrupt?.Invoke(PinNumber);
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
        Controller.SetPinMode(PinNumber, hardwareMode);

        if (isInput)
        {
            // Read current value and register interrupt
            Value = Controller.Read(PinNumber) == PinValue.High;
            if (!wasInput)
            {
                RegisterInterrupt?.Invoke(PinNumber);
            }
        }
        else
        {
            // Write current Value to hardware for output mode
            Controller.Write(PinNumber, Value ? PinValue.High : PinValue.Low);
        }
    }

    /// <summary>
    /// Called before the Value property is set. Writes to hardware in output mode.
    /// </summary>
    partial void OnSetValue(ref bool value)
    {
        if (Controller == null || Mode != GpioPinMode.Output || Status != ServiceStatus.Running)
            return;

        // Write to hardware
        var pinValue = value ? PinValue.High : PinValue.Low;
        Controller.Write(PinNumber, pinValue);

        // Read-back verification
        var actualValue = Controller.Read(PinNumber) == PinValue.High;
        if (actualValue != value)
        {
            // Set status first to prevent recursion (status check above will exit early)
            Status = ServiceStatus.Error;
            StatusMessage = "Write verification failed - possible short circuit or external driver";
        }
    }
}
