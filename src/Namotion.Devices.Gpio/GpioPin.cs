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

    private readonly Lock _lock = new();

    /// <summary>
    /// The GPIO pin number (BCM numbering).
    /// </summary>
    public partial int PinNumber { get; set; }

    /// <summary>
    /// The pin operating mode.
    /// </summary>
    [State]
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
    [Derived]
    public string Title => $"Pin {PinNumber}: {Status} ({Mode}) {Value}";

    /// <inheritdoc />
    public string Icon => "Settings";

    /// <inheritdoc />
    [Derived]
    public string? IconColor => Status switch
    {
        ServiceStatus.Running => Value ? "Success" : "Default",
        ServiceStatus.Error => "Error",
        _ => "Warning"
    };

    public GpioPin()
    {
        PinNumber = 0;
        Mode = GpioPinMode.Input;
        Value = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    /// <summary>
    /// Sets the pin value (only effective in output mode).
    /// </summary>
    /// <param name="value">True for high, false for low.</param>
    [Operation(Title = "Set Value", Icon = "ToggleOn", Position = 1)]
    public void SetValue(bool value)
    {
        Value = value;
    }

    /// <summary>
    /// Called before the Mode property is set. Handles hardware pin mode changes.
    /// </summary>
    partial void OnSetMode(ref GpioPinMode value)
    {
        lock (_lock)
        {
            if (Controller == null || Status != ServiceStatus.Running)
                return;

            var oldMode = Mode;
            var newMode = value;

            if (oldMode == newMode)
                return;

            try
            {
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
            catch (Exception ex)
            {
                Status = ServiceStatus.Error;
                StatusMessage = $"Failed to change GPIO pin {PinNumber} mode to {newMode}: {ex.Message}";
                throw new InvalidOperationException($"Failed to change GPIO pin {PinNumber} mode to {newMode}", ex);
            }
        }
    }

    /// <summary>
    /// Called before the Value property is set. Writes to hardware in output mode.
    /// </summary>
    partial void OnSetValue(ref bool value)
    {
        lock (_lock)
        {
            if (Controller == null || Status != ServiceStatus.Running)
                return;

            // Check hardware mode instead of property Mode to handle recursion during mode change
            if (!Controller.IsPinOpen(PinNumber) || Controller.GetPinMode(PinNumber) != PinMode.Output)
                return;

            try
            {
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
            catch (Exception ex)
            {
                Status = ServiceStatus.Error;
                StatusMessage = $"Failed to write to GPIO pin {PinNumber}: {ex.Message}";
                throw new InvalidOperationException($"Failed to write to GPIO pin {PinNumber}", ex);
            }
        }
    }
}
