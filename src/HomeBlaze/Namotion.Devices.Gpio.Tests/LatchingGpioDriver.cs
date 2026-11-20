using System.Device.Gpio;

namespace Namotion.Devices.Gpio.Tests;

/// <summary>
/// Test double of a register-based GPIO driver, such as the Raspberry Pi one, with 28 pins. Each pin has a mode, an
/// input level (what it reads as an input, such as its power-on pull) and an output latch (initially low, written in
/// any mode). An output reads and drives its latch. Changing the mode does not touch the latch, setting an output with
/// an initial value writes the latch before switching, and closing a pin keeps its mode and latch, like the Raspberry
/// Pi register driver.
/// </summary>
internal sealed class LatchingGpioDriver : GpioDriver
{
    private readonly Lock _lock = new();

    // Guards _eventHandlers. Taken after _lock when a callback is added or removed, and alone while the callbacks of an
    // edge run, like libgpiod 2.x.
    private readonly Lock _callbackLock = new();
    private readonly Dictionary<int, PinState> _pins = new();
    private readonly Dictionary<int, List<PinChangeEventHandler>> _eventHandlers = new();
    private bool _failsReads;

    /// <summary>
    /// Whether the driver behaves like libgpiod 1.x, which requests a line only when its mode is set and releases it
    /// when the pin is closed: <see cref="Read"/> throws for a pin that is not requested, and a line requested as an
    /// output without an initial value drives low.
    /// </summary>
    public bool FailsReadBeforeMode { get; init; }

    /// <summary>
    /// Whether the driver behaves like libgpiod 2.x, which requests a line with its direction as is when the pin is
    /// opened and releases it when the pin is closed: switching a requested line to an output drives low, also a line
    /// that is an output in hardware, and setting an output with an initial value sets the mode first and then writes
    /// the level.
    /// </summary>
    public bool SetsModeBeforeLevel { get; init; }

    /// <summary>
    /// Whether the driver finds no GPIO chip: reading the pin count throws an <see cref="IOException"/>.
    /// </summary>
    public bool IsUnavailable { get; init; }

    /// <summary>
    /// Whether the driver does not support the platform: reading the pin count throws a
    /// <see cref="PlatformNotSupportedException"/>.
    /// </summary>
    public bool IsPlatformNotSupported { get; init; }

    /// <summary>
    /// Whether adding and removing a value changed callback throw an <see cref="IOException"/>, such as in a container
    /// without writable sysfs.
    /// </summary>
    public bool FailsCallbacks { get; init; }

    /// <summary>
    /// Whether disposing the driver with <see cref="IDisposable.Dispose"/> throws an <see cref="IOException"/>.
    /// </summary>
    public bool FailsDispose { get; init; }

    /// <summary>
    /// Whether reading a pin throws an <see cref="IOException"/>.
    /// </summary>
    public bool FailsReads
    {
        get => Volatile.Read(ref _failsReads);
        set => Volatile.Write(ref _failsReads, value);
    }

    /// <summary>
    /// The pin for which the next removal of a value changed callback throws an <see cref="IOException"/>, once.
    /// </summary>
    public int? FailingCallbackRemovalPinNumber { get; set; }

    /// <summary>
    /// Called with the pin number when a pin is closed.
    /// </summary>
    public Action<int>? PinClosed { get; set; }

    /// <summary>
    /// Called with the pin number while a value changed callback is removed, holding the driver lock and before the
    /// callback lock.
    /// </summary>
    public Action<int>? CallbackRemoving { get; set; }

    /// <summary>
    /// Calls the value changed callbacks of a pin for an edge to <paramref name="level"/> while holding the callback
    /// lock, which removing a callback takes after the driver lock, like libgpiod 2.x. The input level is not changed.
    /// <paramref name="dispatching"/> is called under the callback lock right before the callbacks.
    /// </summary>
    public void RaiseEdge(int pinNumber, PinValue level, Action? dispatching = null)
    {
        lock (_callbackLock)
        {
            dispatching?.Invoke();
            if (_eventHandlers.TryGetValue(pinNumber, out var handlers))
            {
                var changeType = level == PinValue.High ? PinEventTypes.Rising : PinEventTypes.Falling;
                foreach (var handler in handlers.ToArray())
                {
                    handler(this, new PinValueChangedEventArgs(changeType, pinNumber));
                }
            }
        }
    }

    /// <summary>
    /// Sets the state a pin has before it is opened.
    /// </summary>
    public void SetUpPin(int pinNumber, PinMode mode, PinValue inputLevel, PinValue latch)
    {
        lock (_lock)
        {
            var state = GetState(pinNumber);
            state.Mode = mode;
            state.InputLevel = inputLevel;
            state.Latch = latch;
            state.IsModeSet = false;
        }
    }

    /// <summary>
    /// Gets the mode of a pin.
    /// </summary>
    public PinMode GetMode(int pinNumber)
    {
        lock (_lock)
        {
            return GetState(pinNumber).Mode;
        }
    }

    /// <summary>
    /// Gets the output latch of a pin.
    /// </summary>
    public PinValue GetLatch(int pinNumber)
    {
        lock (_lock)
        {
            return GetState(pinNumber).Latch;
        }
    }

    /// <summary>
    /// Gets how often the pin was opened.
    /// </summary>
    public int GetOpenCount(int pinNumber)
    {
        lock (_lock)
        {
            return GetState(pinNumber).OpenCount;
        }
    }

    /// <summary>
    /// Gets how often the mode of the pin was set.
    /// </summary>
    public int GetModeSetCount(int pinNumber)
    {
        lock (_lock)
        {
            return GetState(pinNumber).ModeSetCount;
        }
    }

    /// <summary>
    /// Gets every level the pin drove as an output since it was set up, in order.
    /// </summary>
    public PinValue[] GetDrivenLevels(int pinNumber)
    {
        lock (_lock)
        {
            return GetState(pinNumber).DrivenLevels.ToArray();
        }
    }

    protected override int PinCount => IsPlatformNotSupported
        ? throw new PlatformNotSupportedException("No GPIO driver for this platform.")
        : IsUnavailable ? throw new IOException("No GPIO chip found.") : 28;

    protected override void OpenPin(int pinNumber)
    {
        lock (_lock)
        {
            GetState(pinNumber).OpenCount++;
        }
    }

    protected override void ClosePin(int pinNumber)
    {
        lock (_lock)
        {
            if (FailsReadBeforeMode || SetsModeBeforeLevel)
            {
                GetState(pinNumber).IsModeSet = false;
            }
        }

        PinClosed?.Invoke(pinNumber);
    }

    protected override void SetPinMode(int pinNumber, PinMode mode) => SetMode(pinNumber, mode, initialValue: null);

    protected override void SetPinMode(int pinNumber, PinMode mode, PinValue initialValue)
    {
        if (!SetsModeBeforeLevel)
        {
            SetMode(pinNumber, mode, initialValue);
            return;
        }

        SetMode(pinNumber, mode, initialValue: null);
        if (mode == PinMode.Output)
        {
            Write(pinNumber, initialValue);
        }
    }

    protected override PinMode GetPinMode(int pinNumber)
    {
        lock (_lock)
        {
            return GetState(pinNumber).Mode;
        }
    }

    protected override bool IsPinModeSupported(int pinNumber, PinMode mode) => true;

    protected override PinValue Read(int pinNumber)
    {
        lock (_lock)
        {
            var state = GetState(pinNumber);
            if (FailsReads)
            {
                throw new IOException($"Pin {pinNumber} could not be read.");
            }

            if (FailsReadBeforeMode && !state.IsModeSet)
            {
                throw new IOException($"Pin {pinNumber} is not requested.");
            }

            return state.Mode == PinMode.Output ? state.Latch : state.InputLevel;
        }
    }

    protected override void Write(int pinNumber, PinValue value)
    {
        lock (_lock)
        {
            var state = GetState(pinNumber);
            state.Latch = value;
            if (state.Mode == PinMode.Output)
            {
                state.DrivenLevels.Add(value);
            }
        }
    }

    protected override void AddCallbackForPinValueChangedEvent(int pinNumber, PinEventTypes eventTypes, PinChangeEventHandler callback)
    {
        if (FailsCallbacks)
        {
            throw new IOException($"The callback of pin {pinNumber} could not be added.");
        }

        lock (_lock)
        lock (_callbackLock)
        {
            if (!_eventHandlers.TryGetValue(pinNumber, out var handlers))
            {
                handlers = new List<PinChangeEventHandler>();
                _eventHandlers[pinNumber] = handlers;
            }

            handlers.Add(callback);
        }
    }

    protected override void RemoveCallbackForPinValueChangedEvent(int pinNumber, PinChangeEventHandler callback)
    {
        lock (_lock)
        {
            if (FailsCallbacks || FailingCallbackRemovalPinNumber == pinNumber)
            {
                FailingCallbackRemovalPinNumber = null;
                throw new IOException($"The callback of pin {pinNumber} could not be removed.");
            }

            CallbackRemoving?.Invoke(pinNumber);
            lock (_callbackLock)
            {
                if (_eventHandlers.TryGetValue(pinNumber, out var handlers))
                {
                    handlers.Remove(callback);
                }
            }
        }
    }

    protected override WaitForEventResult WaitForEvent(int pinNumber, PinEventTypes eventTypes, CancellationToken cancellationToken)
    {
        return new WaitForEventResult { EventTypes = PinEventTypes.None, TimedOut = true };
    }

    private void SetMode(int pinNumber, PinMode mode, PinValue? initialValue)
    {
        lock (_lock)
        {
            var state = GetState(pinNumber);
            var isOutputRequested = mode == PinMode.Output &&
                ((FailsReadBeforeMode && !state.IsModeSet) || (SetsModeBeforeLevel && (!state.IsModeSet || state.Mode != PinMode.Output)));
            var latch = initialValue ?? (isOutputRequested ? PinValue.Low : state.Latch);
            if (mode == PinMode.Output && (state.Mode != PinMode.Output || !state.IsModeSet || latch != state.Latch))
            {
                state.DrivenLevels.Add(latch);
            }

            state.Latch = latch;
            state.Mode = mode;
            state.IsModeSet = true;
            state.ModeSetCount++;
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        // Not on the finalizer thread, where a thrown exception would end the test host.
        if (FailsDispose && disposing)
        {
            throw new IOException("The driver could not be disposed.");
        }
    }

    private PinState GetState(int pinNumber)
    {
        if (!_pins.TryGetValue(pinNumber, out var state))
        {
            state = new PinState();
            _pins[pinNumber] = state;
        }

        return state;
    }

    private sealed class PinState
    {
        public PinMode Mode { get; set; } = PinMode.Input;

        public PinValue InputLevel { get; set; } = PinValue.Low;

        public PinValue Latch { get; set; } = PinValue.Low;

        public bool IsModeSet { get; set; }

        public int OpenCount { get; set; }

        public int ModeSetCount { get; set; }

        public List<PinValue> DrivenLevels { get; } = new();
    }
}
