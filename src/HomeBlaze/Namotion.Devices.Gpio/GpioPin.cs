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

    // 1 while a read of the level for an edge that found the pin busy is queued, so that a burst of edges queues one.
    private int _isBusyEdgeReadQueued;

    // Guarded by _lock. Whether the pin is open as an output on Controller, so that an edge checks it without calling
    // the driver.
    private bool _isOutputInHardware;

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
    [Operation(Title = "Turn On", Icon = "ToggleOn", Position = 1, Description = "Sets an output pin on. Only for an output pin that is running.")]
    public Task TurnOnAsync(CancellationToken cancellationToken) => SetValueAsync(true, cancellationToken);

    /// <summary>
    /// Sets an output pin off.
    /// </summary>
    /// <exception cref="InvalidOperationException">The pin is an input.</exception>
    [Operation(Title = "Turn Off", Icon = "ToggleOff", Position = 2, Description = "Sets an output pin off. Only for an output pin that is running.")]
    public Task TurnOffAsync(CancellationToken cancellationToken) => SetValueAsync(false, cancellationToken);

    /// <summary>
    /// Inverts the logical value of an output pin.
    /// </summary>
    /// <exception cref="InvalidOperationException">The pin is an input.</exception>
    [Operation(Title = "Toggle", Position = 3, Description = "Inverts the value of an output pin. Only for an output pin that is running.")]
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
    [Operation(Title = "Set Value", Position = 4, Description = "Sets the logical value of an output pin. Written to the pin while it is running.")]
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

                if (!isInput)
                {
                    // On a pin that is not an output, the controller only stores the level and passes it to the
                    // driver as the initial value of the mode change, so an input becomes an output at its level.
                    Controller.Write(PinNumber, ToLevel(Value));
                }

                Controller.SetPinMode(PinNumber, ToHardwareMode(newMode));
                _isOutputInHardware = !isInput;

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

            // The hardware mode instead of the Mode property, for the recursion during a mode change; and not asked from
            // the driver, because an edge sets the value of an input on the driver's thread (see ApplyHardwareLevel).
            if (!_isOutputInHardware)
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
    /// an output in hardware. When the pin is busy, its level is read and applied on the thread pool instead.
    /// </summary>
    internal void ApplyHardwareLevel(bool isHigh)
    {
        // Never waits for the lock and never calls the driver: drivers such as libgpiod 2.x call this while holding a
        // lock that removing a callback also takes, after their own lock, and a mode change removes the callback under
        // the pin lock.
        if (!_lock.TryEnter())
        {
            if (Interlocked.Exchange(ref _isBusyEdgeReadQueued, 1) == 0)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static pin => pin.ReadLevelForBusyEdge(), this, preferLocal: false);
            }

            return;
        }

        try
        {
            if (Controller == null || Status != ServiceStatus.Running || _debounceTimer is not null)
                return;

            // An edge reported before the pin became an output would write its stale level to the output. The hardware
            // mode also covers the window before the Mode property is stored.
            if (_isOutputInHardware)
                return;

            SetValueFromLevel(isHigh);
        }
        finally
        {
            _lock.Exit();
        }
    }

    private void ReadLevelForBusyEdge()
    {
        // Cleared before the read, so that an edge during the read queues another one.
        Volatile.Write(ref _isBusyEdgeReadQueued, 0);
        try
        {
            PollHardwareLevel();
        }
        catch (Exception exception)
        {
            // On the thread pool, where a thrown exception would end the process.
            try
            {
                lock (_lock)
                {
                    // A pin detached or attached again since the read has a status of its own.
                    if (Controller == null || Status != ServiceStatus.Running)
                        return;

                    StatusMessage = $"Failed to read GPIO pin {PinNumber} after an edge: {exception.Message}";
                    Status = ServiceStatus.Error;
                }
            }
            catch (Exception)
            {
                // Nothing is left to report it to.
            }
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
    /// Opens the pin on <paramref name="controller"/> in its <see cref="Mode"/> and starts it, or sets
    /// <see cref="Status"/> to <see cref="ServiceStatus.Unavailable"/> with the reason. An output opens at the level
    /// the pin has. Called with the pin number set and the pin closed on the controller, or open as an output: then an
    /// output keeps its line and its level, and another mode closes it first.
    /// </summary>
    internal void Attach(GpioController controller, Action<int> registerInterrupt, Action<int> unregisterInterrupt)
    {
        // Under the lock, so that a mode change waits until the pin runs and is then applied to the hardware.
        lock (_lock)
        {
            CancelDebounce();
            Controller = controller;
            RegisterInterrupt = registerInterrupt;
            UnregisterInterrupt = unregisterInterrupt;

            PinValue level;
            try
            {
                var isOpenAsOutput = controller.IsPinOpen(PinNumber) && controller.GetPinMode(PinNumber) == PinMode.Output;
                if (Mode == GpioPinMode.Output)
                {
                    // Opening the line again would drive it low for a moment on drivers that set the direction before
                    // the level, such as libgpiod 2.x.
                    level = isOpenAsOutput ? controller.Read(PinNumber) : OpenOutput(controller);
                }
                else
                {
                    if (isOpenAsOutput)
                    {
                        controller.ClosePin(PinNumber);
                    }

                    controller.OpenPin(PinNumber, ToHardwareMode(Mode));
                    level = controller.Read(PinNumber);

                    // Registered before the pin reports Running, so that an edge after that has a handler.
                    registerInterrupt(PinNumber);
                }

                _isOutputInHardware = Mode == GpioPinMode.Output;
            }
            catch (Exception exception)
            {
                _isOutputInHardware = false;
                StatusMessage = exception.Message;
                Status = ServiceStatus.Unavailable;
                return;
            }

            // Outside the try, so that a throwing subscriber reaches the caller instead of reporting the pin unavailable.
            Value = ToValue(level);
            StatusMessage = null;
            Status = ServiceStatus.Running;
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
            _isOutputInHardware = false;
            StatusMessage = null;
            Status = ServiceStatus.Stopped;
        }
    }

    internal PinValue ToLevel(bool value) => value != IsInverted ? PinValue.High : PinValue.Low;

    internal bool ToValue(PinValue level) => (level == PinValue.High) != IsInverted;

    private static bool IsInputMode(GpioPinMode mode) => mode != GpioPinMode.Output;

    private static PinMode ToHardwareMode(GpioPinMode mode) => mode switch
    {
        GpioPinMode.InputPullUp => PinMode.InputPullUp,
        GpioPinMode.InputPullDown => PinMode.InputPullDown,
        GpioPinMode.Output => PinMode.Output,
        _ => PinMode.Input
    };

    // Called under _lock. Opens the pin as an output and returns its level.
    private PinValue OpenOutput(GpioController controller)
    {
        // The level is read before the mode is set, so an output keeps the level it has: the latch after a restart,
        // the power-on pull after a reboot. This relies on opening without a mode not changing the pin. A driver that
        // cannot read a pin before its mode is set (libgpiod 1.x) opens it as an output its own way, and the level is
        // read back from it.
        controller.OpenPin(PinNumber);
        if (TryReadLevel(controller, PinNumber, out var levelBeforeMode))
        {
            controller.OpenPin(PinNumber, PinMode.Output, levelBeforeMode);
            return levelBeforeMode;
        }

        controller.OpenPin(PinNumber, PinMode.Output);
        return controller.Read(PinNumber);
    }

    private static bool TryReadLevel(GpioController controller, int pinNumber, out PinValue level)
    {
        try
        {
            level = controller.Read(pinNumber);
            return true;
        }
        catch (IOException)
        {
            // The libgpiod V1 driver reads a pin only once its mode is set.
            level = default;
            return false;
        }
    }

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
