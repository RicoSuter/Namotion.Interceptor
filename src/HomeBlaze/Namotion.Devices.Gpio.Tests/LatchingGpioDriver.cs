using System.Device.Gpio;

namespace Namotion.Devices.Gpio.Tests;

/// <summary>
/// Test double of a register-based GPIO driver, such as the Raspberry Pi one, with 28 pins. Each pin has a mode, an
/// input level (what it reads as an input, such as its power-on pull) and an output latch (initially low, written in
/// any mode). An output reads and drives its latch. Changing the mode does not touch the latch, and setting an output
/// with an initial value writes the latch before switching, like the Raspberry Pi register driver.
/// </summary>
internal sealed class LatchingGpioDriver : GpioDriver
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, PinState> _pins = new();
    private readonly Dictionary<int, List<PinChangeEventHandler>> _eventHandlers = new();

    /// <summary>
    /// Whether <see cref="Read"/> throws for a pin whose mode was not set since it was set up, like libgpiod, which
    /// requests a line only when its mode is set.
    /// </summary>
    public bool FailsReadBeforeMode { get; init; }

    /// <summary>
    /// Called after the mode of a pin was set, outside the lock of the driver, with the pin number.
    /// </summary>
    public Action<int>? PinModeSet { get; set; }

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

    protected override int PinCount => 28;

    protected override int ConvertPinNumberToLogicalNumberingScheme(int pinNumber) => pinNumber;

    protected override void OpenPin(int pinNumber)
    {
        lock (_lock)
        {
            GetState(pinNumber).OpenCount++;
        }
    }

    protected override void ClosePin(int pinNumber)
    {
    }

    protected override void SetPinMode(int pinNumber, PinMode mode)
    {
        lock (_lock)
        {
            var state = GetState(pinNumber);
            if (mode == PinMode.Output && state.Mode != PinMode.Output)
            {
                state.DrivenLevels.Add(state.Latch);
            }

            state.Mode = mode;
            state.IsModeSet = true;
            state.ModeSetCount++;
        }

        PinModeSet?.Invoke(pinNumber);
    }

    protected override void SetPinMode(int pinNumber, PinMode mode, PinValue initialValue)
    {
        Write(pinNumber, initialValue);
        SetPinMode(pinNumber, mode);
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
        lock (_lock)
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
            if (_eventHandlers.TryGetValue(pinNumber, out var handlers))
            {
                handlers.Remove(callback);
            }
        }
    }

    protected override WaitForEventResult WaitForEvent(int pinNumber, PinEventTypes eventTypes, CancellationToken cancellationToken)
    {
        return new WaitForEventResult { EventTypes = PinEventTypes.None, TimedOut = true };
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
