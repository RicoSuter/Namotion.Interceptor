using System.Device.Gpio;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Gpio;

/// <summary>
/// Represents a single GPIO pin with mode, value, and availability status.
/// </summary>
[InterceptorSubject]
public partial class GpioPin : IHostedSubject, ITitleProvider, IIconProvider, ISwitchDevice
{
    internal GpioController? Controller { get; set; }
    internal Action<int>? RegisterInterrupt { get; set; }
    internal Action<int>? UnregisterInterrupt { get; set; }

    private readonly Lock _lock = new();

    /// <summary>
    /// The GPIO pin number (BCM numbering).
    /// </summary>
    [State]
    public partial int PinNumber { get; set; }

    /// <summary>
    /// Optional friendly name for the pin.
    /// </summary>
    [Configuration]
    public partial string? Name { get; set; }

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
    public string Title => string.IsNullOrEmpty(Name)
        ? $"Pin {PinNumber}"
        : $"{Name} (Pin {PinNumber})";

    /// <inheritdoc />
    [Derived]
    public string IconName => Status switch
    {
        ServiceStatus.Running => Value ? "ToggleOn" : "ToggleOff",
        ServiceStatus.Error => "Error",
        ServiceStatus.Unavailable => "Block",
        _ => "Warning"
    };

    /// <inheritdoc />
    [Derived]
    public string? IconColor => Status switch
    {
        ServiceStatus.Running => Value ? "Success" : "Default",
        ServiceStatus.Error => "Error",
        ServiceStatus.Unavailable => "Default",
        _ => "Warning"
    };

    /// <inheritdoc />
    [Derived]
    public bool? IsOn => Mode == GpioPinMode.Output ? Value : null;

    public GpioPin()
    {
        PinNumber = 0;
        Name = null;
        Mode = GpioPinMode.Input;
        Value = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }
    
    [Derived]
    [PropertyAttribute("TurnOn", KnownAttributes.IsEnabled)]
    public bool TurnOn_IsEnabled => Mode == GpioPinMode.Output && Status == ServiceStatus.Running && !Value;

    [Derived]
    [PropertyAttribute("TurnOff", KnownAttributes.IsEnabled)]
    public bool TurnOff_IsEnabled => Mode == GpioPinMode.Output && Status == ServiceStatus.Running && Value;

    [Derived]
    [PropertyAttribute("Toggle", KnownAttributes.IsEnabled)]
    public bool Toggle_IsEnabled => Mode == GpioPinMode.Output && Status == ServiceStatus.Running;

    /// <inheritdoc />
    [Operation(Title = "Turn On", Icon = "ToggleOn", Position = 1)]
    public Task TurnOnAsync(CancellationToken cancellationToken)
    {
        Value = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    [Operation(Title = "Turn Off", Icon = "ToggleOff", Position = 2)]
    public Task TurnOffAsync(CancellationToken cancellationToken)
    {
        Value = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called before the Mode property is set. Handles hardware pin mode changes.
    /// </summary>
    partial void OnModeChanging(ref GpioPinMode newValue, ref bool cancel)
    {
        lock (_lock)
        {
            if (Controller == null || Status != ServiceStatus.Running)
                return;

            var oldMode = Mode;
            var newMode = newValue;

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
    partial void OnValueChanging(ref bool newValue, ref bool cancel)
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
                var pinValue = newValue ? PinValue.High : PinValue.Low;
                Controller.Write(PinNumber, pinValue);

                // Read-back verification
                var actualValue = Controller.Read(PinNumber) == PinValue.High;
                if (actualValue != newValue)
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
