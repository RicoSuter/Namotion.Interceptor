using System.Device.Gpio;

namespace Namotion.Devices.Gpio.Simulation;

/// <summary>
/// Simulation GPIO driver for testing GPIO functionality without real hardware.
/// Simulates 28 GPIO pins (BCM 0-27) with in-memory state tracking.
/// </summary>
public class SimulationGpioDriver : GpioDriver
{
    private readonly int _pinCount;
    private readonly Dictionary<int, PinValue> _pinValues = new();
    private readonly Dictionary<int, PinMode> _pinModes = new();
    private readonly Dictionary<int, List<PinChangeEventHandler>> _eventHandlers = new();

    /// <summary>
    /// Creates a SimulationGpioDriver with the default pin count (28 pins, BCM 0-27).
    /// </summary>
    public SimulationGpioDriver() : this(28)
    {
    }

    /// <summary>
    /// Creates a SimulationGpioDriver with a custom pin count.
    /// </summary>
    /// <param name="pinCount">The number of GPIO pins to simulate.</param>
    public SimulationGpioDriver(int pinCount)
    {
        _pinCount = pinCount;
    }

    /// <summary>
    /// Gets the current pin values for verification.
    /// </summary>
    public IReadOnlyDictionary<int, PinValue> PinValues => _pinValues;

    /// <summary>
    /// Gets the current pin modes for verification.
    /// </summary>
    public IReadOnlyDictionary<int, PinMode> PinModes => _pinModes;

    /// <summary>
    /// Gets write call history for verification.
    /// </summary>
    public List<(int PinNumber, PinValue Value)> WriteHistory { get; } = new();

    /// <summary>
    /// Simulates a pin value change (as if hardware changed).
    /// </summary>
    public void SimulatePinValueChange(int pinNumber, PinValue newValue)
    {
        _pinValues[pinNumber] = newValue;

        if (_eventHandlers.TryGetValue(pinNumber, out var handlers))
        {
            var changeType = newValue == PinValue.High ? PinEventTypes.Rising : PinEventTypes.Falling;
            var args = new PinValueChangedEventArgs(changeType, pinNumber);
            foreach (var handler in handlers.ToList())
            {
                handler(this, args);
            }
        }
    }

    protected override int PinCount => _pinCount;

    protected override int ConvertPinNumberToLogicalNumberingScheme(int pinNumber) => pinNumber;

    protected override void OpenPin(int pinNumber)
    {
        if (!_pinValues.ContainsKey(pinNumber))
        {
            _pinValues[pinNumber] = PinValue.Low;
        }
        if (!_pinModes.ContainsKey(pinNumber))
        {
            _pinModes[pinNumber] = PinMode.Input;
        }
    }

    protected override void ClosePin(int pinNumber)
    {
    }

    protected override void SetPinMode(int pinNumber, PinMode mode)
    {
        _pinModes[pinNumber] = mode;
    }

    protected override PinMode GetPinMode(int pinNumber)
    {
        return _pinModes.GetValueOrDefault(pinNumber, PinMode.Input);
    }

    protected override bool IsPinModeSupported(int pinNumber, PinMode mode) => true;

    protected override PinValue Read(int pinNumber)
    {
        return _pinValues.GetValueOrDefault(pinNumber, PinValue.Low);
    }

    protected override void Write(int pinNumber, PinValue value)
    {
        WriteHistory.Add((pinNumber, value));
        _pinValues[pinNumber] = value;
    }

    protected override void AddCallbackForPinValueChangedEvent(int pinNumber, PinEventTypes eventTypes, PinChangeEventHandler callback)
    {
        if (!_eventHandlers.ContainsKey(pinNumber))
        {
            _eventHandlers[pinNumber] = new List<PinChangeEventHandler>();
        }
        _eventHandlers[pinNumber].Add(callback);
    }

    protected override void RemoveCallbackForPinValueChangedEvent(int pinNumber, PinChangeEventHandler callback)
    {
        if (_eventHandlers.TryGetValue(pinNumber, out var handlers))
        {
            handlers.Remove(callback);
        }
    }

    protected override WaitForEventResult WaitForEvent(int pinNumber, PinEventTypes eventTypes, CancellationToken cancellationToken)
    {
        return new WaitForEventResult { EventTypes = PinEventTypes.None, TimedOut = true };
    }
}
