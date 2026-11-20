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
public partial class GpioPin : IMonitoredService, ITitleProvider, IIconProvider, ISwitchDevice
{
    internal GpioController? Controller { get; set; }
    internal Action<int>? RegisterInterrupt { get; set; }
    internal Action<int>? UnregisterInterrupt { get; set; }
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// The longest <see cref="DebounceTime"/> that is accepted.
    /// </summary>
    public static readonly TimeSpan MaximumDebounceTime = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private readonly TimerCallback _onDebounceElapsed;

    // Guarded by _lock. A callback of a window that was canceled or replaced sees another generation and does nothing.
    private ITimer? _debounceTimer;
    private long _debounceGeneration;

    /// <summary>
    /// The GPIO pin number (BCM numbering).
    /// </summary>
    [State(Position = 0)]
    public partial int PinNumber { get; internal set; }

    /// <summary>
    /// Optional friendly name for the pin.
    /// </summary>
    [Configuration]
    public partial string? Name { get; set; }

    /// <summary>
    /// The pin operating mode.
    /// </summary>
    [State(Position = 1)]
    [Configuration]
    public partial GpioPinMode Mode { get; set; }

    /// <summary>
    /// Whether <see cref="Value"/> is the inverse of the level of the pin, such as for an active-low relay or a button
    /// that pulls an <see cref="GpioPinMode.InputPullUp"/> pin to ground.
    /// </summary>
    [Configuration]
    public partial bool IsInverted { get; set; }

    /// <summary>
    /// How long an input ignores further edges after a change of <see cref="Value"/>, so that a bouncing contact
    /// changes it once. The level is read again when the time elapsed. Zero disables debouncing; outputs ignore it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or above <see cref="MaximumDebounceTime"/>.</exception>
    [Configuration]
    public partial TimeSpan DebounceTime { get; set; }

    /// <summary>
    /// The current logical value of the pin: the level of the pin (true = high), inverted when <see cref="IsInverted"/> is set.
    /// </summary>
    [State(Position = 2)]
    public partial bool Value { get; internal set; }

    /// <summary>
    /// Pin availability status.
    /// </summary>
    [State]
    public partial ServiceStatus Status { get; internal set; }

    /// <summary>
    /// Status message (e.g., "Reserved for I2C", "Write verification failed").
    /// </summary>
    [State]
    public partial string? StatusMessage { get; internal set; }

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
    public string? IconColor => Status == ServiceStatus.Running && Value ? "Success" : null;

    /// <inheritdoc />
    [Derived]
    public bool? IsOn => Mode == GpioPinMode.Output ? Value : null;

    public GpioPin()
    {
        _onDebounceElapsed = OnDebounceElapsed;

        PinNumber = 0;
        Name = null;
        Mode = GpioPinMode.Input;
        IsInverted = false;
        DebounceTime = TimeSpan.FromMilliseconds(50);
        Value = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }
    
    private bool IsRunningOutput => Mode == GpioPinMode.Output && Status == ServiceStatus.Running;

    [Derived]
    [PropertyAttribute("TurnOn", KnownAttributes.IsEnabled)]
    public bool TurnOn_IsEnabled => IsRunningOutput && !Value;

    [Derived]
    [PropertyAttribute("TurnOff", KnownAttributes.IsEnabled)]
    public bool TurnOff_IsEnabled => IsRunningOutput && Value;

    [Derived]
    [PropertyAttribute("Toggle", KnownAttributes.IsEnabled)]
    public bool Toggle_IsEnabled => IsRunningOutput;

    [Derived]
    [PropertyAttribute("SetValue", KnownAttributes.IsEnabled)]
    public bool SetValue_IsEnabled => IsRunningOutput;

    /// <summary>
    /// Sets an output pin on.
    /// </summary>
    /// <exception cref="InvalidOperationException">The pin is an input.</exception>
    [Operation(Title = "Turn On", Icon = "ToggleOn", Position = 1)]
    public Task TurnOnAsync(CancellationToken cancellationToken) => SetValueAsync(true, cancellationToken);

    /// <summary>
    /// Sets an output pin off.
    /// </summary>
    /// <exception cref="InvalidOperationException">The pin is an input.</exception>
    [Operation(Title = "Turn Off", Icon = "ToggleOff", Position = 2)]
    public Task TurnOffAsync(CancellationToken cancellationToken) => SetValueAsync(false, cancellationToken);

    /// <summary>
    /// Inverts the logical value of an output pin.
    /// </summary>
    /// <exception cref="InvalidOperationException">The pin is an input.</exception>
    [Operation(Title = "Toggle", Position = 3)]
    public Task ToggleAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ThrowIfInput();
            Value = !Value;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the logical value of an output pin: the level written is inverted when <see cref="IsInverted"/> is set.
    /// </summary>
    /// <remarks>
    /// While the pin is not running, only <see cref="Value"/> changes and nothing is written; the level is read from the pin when it starts.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The pin is an input.</exception>
    [Operation(Title = "Set Value", Position = 4, Description = "Sets the logical value of an output pin.")]
    public Task SetValueAsync(bool value, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ThrowIfInput();
            Value = value;
        }

        return Task.CompletedTask;
    }

    private void ThrowIfInput()
    {
        if (IsInputMode(Mode))
        {
            throw new InvalidOperationException($"Pin {PinNumber} is an input; its value follows the hardware.");
        }
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

            CancelDebounce();

            try
            {
                var wasInput = IsInputMode(oldMode);
                var isInput = IsInputMode(newMode);

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
                if (!isInput)
                {
                    // On a pin that is not an output, the controller only stores the level and passes it to the
                    // driver as the initial value of the mode change, so an input becomes an output at its level.
                    Controller.Write(PinNumber, ToLevel(Value));
                }

                Controller.SetPinMode(PinNumber, hardwareMode);

                if (isInput)
                {
                    // Read current value and register interrupt
                    Value = ToValue(Controller.Read(PinNumber));
                    if (!wasInput)
                    {
                        RegisterInterrupt?.Invoke(PinNumber);
                    }
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
                var level = ToLevel(newValue);
                Controller.Write(PinNumber, level);

                if (Controller.Read(PinNumber) != level)
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

    partial void OnDebounceTimeChanging(ref TimeSpan newValue, ref bool cancel)
    {
        if (newValue < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DebounceTime), newValue, "The debounce time must not be negative.");
        }

        if (newValue > MaximumDebounceTime)
        {
            throw new ArgumentOutOfRangeException(nameof(DebounceTime), newValue, $"The debounce time must not exceed {MaximumDebounceTime.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>
    /// Keeps the level of the pin and reads the value again, so that changing the inversion never switches an output.
    /// </summary>
    partial void OnIsInvertedChanged(bool newValue)
    {
        lock (_lock)
        {
            if (Controller == null || Status != ServiceStatus.Running || !Controller.IsPinOpen(PinNumber))
                return;

            CancelDebounce();

            try
            {
                Value = ToValue(Controller.Read(PinNumber));
            }
            catch (Exception ex)
            {
                Status = ServiceStatus.Error;
                StatusMessage = $"Failed to read GPIO pin {PinNumber}: {ex.Message}";
                throw new InvalidOperationException($"Failed to read GPIO pin {PinNumber}", ex);
            }
        }
    }

    /// <summary>
    /// Applies a level that an interrupt reported. It is ignored while a debounce window is open, and when the pin is
    /// an output in hardware.
    /// </summary>
    internal void ApplyHardwareLevel(bool isHigh)
    {
        lock (_lock)
        {
            if (Controller == null || Status != ServiceStatus.Running || _debounceTimer is not null)
                return;

            // An edge that waited for the lock while the pin became an output would write its stale level to the output.
            // The hardware mode also covers the window before the Mode property is stored.
            if (!Controller.IsPinOpen(PinNumber) || Controller.GetPinMode(PinNumber) == PinMode.Output)
                return;

            SetValueFromLevel(isHigh);
        }
    }

    /// <summary>
    /// Reads the level of the pin and applies it, ignored while a debounce window is open. This corrects an output too.
    /// The read and the apply happen under one lock, so that a write that happens in between is not undone by a stale level.
    /// </summary>
    internal void PollHardwareLevel()
    {
        lock (_lock)
        {
            if (Controller == null || Status != ServiceStatus.Running || _debounceTimer is not null)
                return;

            SetValueFromLevel(Controller.Read(PinNumber) == PinValue.High);
        }
    }

    /// <summary>
    /// Closes an open debounce window without reading the level.
    /// </summary>
    internal void CancelDebounce()
    {
        lock (_lock)
        {
            _debounceGeneration++;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    /// <summary>
    /// Releases the controller and stops the pin. Call it before the controller is disposed, so that an interrupt or
    /// poll that is still in flight cannot open a debounce window on it.
    /// </summary>
    internal void Detach()
    {
        lock (_lock)
        {
            CancelDebounce();
            Controller = null;
            Status = ServiceStatus.Stopped;
            StatusMessage = null;
        }
    }

    internal PinValue ToLevel(bool value) => value != IsInverted ? PinValue.High : PinValue.Low;

    internal bool ToValue(PinValue level) => (level == PinValue.High) != IsInverted;

    private static bool IsInputMode(GpioPinMode mode) => mode != GpioPinMode.Output;

    // Called under _lock.
    private void SetValueFromLevel(bool isHigh)
    {
        var value = isHigh != IsInverted;
        if (value == Value)
            return;

        Value = value;

        if (DebounceTime > TimeSpan.Zero && IsInputMode(Mode))
        {
            var generation = ++_debounceGeneration;
            _debounceTimer = TimeProvider.CreateTimer(_onDebounceElapsed, generation, DebounceTime, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        lock (_lock)
        {
            if ((long)state! != _debounceGeneration)
                return;

            _debounceTimer?.Dispose();
            _debounceTimer = null;

            if (Controller == null || Status != ServiceStatus.Running || !IsInputMode(Mode))
                return;

            try
            {
                SetValueFromLevel(Controller.Read(PinNumber) == PinValue.High);
            }
            catch (Exception ex)
            {
                // Runs on a timer thread, where a thrown exception would end the process.
                Status = ServiceStatus.Error;
                StatusMessage = $"Failed to read GPIO pin {PinNumber}: {ex.Message}";
            }
        }
    }
}
